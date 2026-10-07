# Metal tessellation in Unity 2022.3.62f2 (ground truth)

How Unity 2022.3.62f2 encodes a tessellated pass (vertex + hull + domain + fragment) for Metal in a macOS player, and how
far our HLSLcc gets on the same shader's D3D11 bytecode. Measured 2026-10-07 from a throwaway project built both ways.
The FurShader is the only game shader this affects (`Shader Graphs/FurShader`, D3D gpu types 16/21/22/18).

## TL;DR

1. **Metal has no hull/domain programs.** Each D3D (VS[i], HS[i], DS[i]) triple becomes ONE Metal `progVertex[i]`
   (gpu type 23) holding all three stages in one MSL source. Metal `progHull`/`progDomain` are emptied.
2. **The 5 "zero" header fields are the tessellation info**, in exactly HLSLcc's callback order:
   `OnTessellationInfo(partition, winding, maxFactor, patchesPerThreadGroup)` then `OnTessellationKernelInfo(bufferCount)`.
3. **Entry points:** `kernel void patchKernel` (VS+HS as a compute kernel) and
   `[[patch(triangle, 3)]] vertex ... xlatMtlMain` (DS as a post-tessellation vertex function). The header still
   names `xlatMtlMain`.
4. **Our HLSLcc reproduces Unity's output almost line for line** when VS → HS → DS are translated with ONE shared
   `GLSLCrossDependencyData` and flag `0x2020080`. Header values match on every test shader. The fixes from
   section 6 are **implemented** (section 7): all 24 unique non-instanced FurShader programs compile with the OS
   Metal compiler.
5. **Instanced variants.** The STEREO_INSTANCING variants pass or fail exactly as the hybrid's own Unity-compiled
   ones do. Unity's own Metal output fails the same way on an equivalent test shader, so these are Unity's bugs,
   kept on purpose.

## Test setup (reproducible)

- **Project:** PC `C:\Users\Kieran\Projects\lethal\tesstest` (built-in RP, made with `-batchmode -createProject`).
- **Shaders** in `Assets/` (all our own code):

  | Shader | Domain | Partitioning | Topology | maxtessfactor | Notable |
  |---|---|---|---|---|---|
  | `TessTest/HDRPStyle` | tri, 3 CP | fractional_odd | triangle_cw | 64 | mirrors HDRP `TessellationShare.hlsl`: pass-through hull, distance factors from `_TessellationFactor`, Phong in domain |
  | `TessTest/DomainTex` | tri, 3 CP | integer | triangle_ccw | 15 | texture sampled in domain, patch constant `float3 : TEXCOORD5`, hull writes per-CP data |
  | `TessTest/QuadEven` | quad, 4 CP | fractional_even | triangle_cw | none | |
  | `TessTest/MultiCB` | tri, 3 CP | fractional_odd | triangle_cw | 64 | `HLSLPROGRAM` with real cbuffers placed like the FurShader's (same cbuffer at different registers per stage, a domain-only cbuffer and texture, a VS texture, domain reads `SV_InsideTessFactor`) |
  | `TessTest/Instanced` | tri, 3 CP | fractional_odd | triangle_cw | none | `SV_InstanceID`/`SV_VertexID`, `nointerpolation uint : CUSTOM_INSTANCE_ID` CP member (HDRP pattern), bit-insert helper in hull and domain, `multi_compile _ FOO_ON` |

- **Build:** `Assets/Editor/TessBuild.cs` (`TessBuild.All`) makes the materials, scene and build list, then builds
  StandaloneOSX (Metal only, ARM64) and StandaloneWindows64 (D3D11 only). Each build takes about 2 s incremental.
  - Run: `ssh pc "powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\Kieran\Projects\lethal\tess-build.ps1"`.
  - It goes through `tess.ps1`, which runs the editor via the `TessTestEditor` scheduled task in the desktop session
    (licensing).
- **Extract:** copy `globalgamemanagers*`, `level0`, `sharedassets0.assets`, `Resources/unity_builtin_extra` (≈0.5 MB)
  from `Builds/Mac/TessTest.app/Contents/Resources/Data` and `Builds/Win/TessTest_Data`. Then run
  `lmc dump <data> "TessTest/<name>" <out>`.
  - `ShaderDump` now also writes each parameter entry (`*.params`) and lists `m_CommonParameters` in full.
- **Harness** (not in the repo; copy at PC `tesstest\harness\`): `tesscc.cpp` `#include`s `tools/xlat/xlat.cpp` and links the cached HLSLcc
  objects.
  - It translates VS, HS, DS in that order through one `GLSLCrossDependencyData`, using `rdef::Build` with the desc
    from `LMC_XLAT_DEBUG=1 lmc xlat`, then concatenates the outputs with Unity's stage markers.
  - Compile checks use `tools/xlat/mtlcheck.swift`.

## 1. Program slots and types

| | D3D11 (platform 4) | Metal (platform 14) |
|---|---|---|
| `progVertex[3][i]` | gpu **16** (VS SM5) | gpu **23**: **VS + HS + DS combined** |
| `progHull[3][i]` | gpu **21** | `m_PlayerSubPrograms = []`, `m_ParameterBlobIndices = []` (outer array size 0, not 4 empty tiers); `m_CommonParameters` all empty |
| `progDomain[3][i]` | gpu **22** | same as hull: empty |
| `progFragment[3][i]` | gpu **18** | gpu **24**, an ordinary fragment program (nothing tessellation-specific) |
| `stageCounts[0]` | **4** | **2** |

- **Pairing is by index:** D3D VS[i], HS[i] and DS[i] have identical keyword sets and count, and Metal VS[i] is the
  combination of the three.
  - Verified for all 6 FurShader passes (8/32/8/32/16/16 triples, all index-aligned) and for `Instanced`
    (`[]`, `[FOO_ON]`).
  - D3D VS[0] and VS[1] are separate blobs even when the keyword only changes the hull. Dedupe the combined program
    by (VS blob, HS blob, DS blob, params), not by VS alone.
- **Unchanged:** `m_ShaderRequirements` is identical on both platforms for every program (1298411 = 0x13CFEB, the
  `#pragma target 5.0` set; the FurShader has the same value).
  - Keywords and `m_KeywordNames` are identical. Unity adds no `UNITY_*TESSELLATION*` keyword.
  - `m_SerializedKeywordStateMask` and every other pass field match.
  - Apart from the program lists above, the only differences are `m_NameIndices` (new name table), `m_EditorDataHash`,
    `m_Platforms`, `platforms`, the blob and `stageCounts`.
- **`stageCounts`** looks like "distinct stage types present": 2 for normal shaders, 3 for the geometry-shader blit,
  4 for tessellated shaders on D3D. **The converter must write 2 for the FurShader.**

## 2. Metal program blob header (48 bytes, 12 × u32)

| Offset | Meaning | Non-tess | Tessellated (combined VS) |
|---|---|---|---|
| 0 | magic `0xf00dcafe` | ✓ | ✓ |
| 4 | entry-name offset (= 48) | 48 | 48 |
| 8 | format | 6 | 6 |
| 12 | source offset (48 + `"xlatMtlMain\0"`) | 60 | 60 |
| **16** | **partition mode** (`MTLTessellationPartitionMode`: 0 pow2, 1 integer, 2 fractional_odd, 3 fractional_even) | 0 | see below |
| **20** | **output winding** (`MTLWinding`: 0 CW, 1 CCW). **Flipped from HLSL:** `triangle_cw` → 1, `triangle_ccw` → 0 (HLSLcc `toMetalDeclaration.cpp` swaps them on purpose) | 0 | |
| **24** | **max tess factor**, `(uint)` of `[maxtessfactor(x)]`; **64 when the attribute is absent** | 0 | |
| **28** | **patches per threadgroup** = 32 / max(input CPs, output CPs): **10** for 3 CPs, **8** for 4 | 0 | |
| **32** | **patch-kernel buffer count** = `buffer()` slots allocated by VS + HS = the kernel's `controlPoints` index | 0 | |
| 36 | flags | 37 | 37 |
| 40 | colour write mask | ~0 | ~0 (the combined VS has no fragment outputs) |
| 44 | 0 | 0 | 0 |

**Observed values for offsets 16–35** (fragment programs are always 0,0,0,0,0):

| Shader | Unity header | Our HLSLcc reflection (same DXBC from the D3D build) |
|---|---|---|
| HDRPStyle (frac_odd, cw, 64) | 2, 1, 64, 10, **1** | `tess 2 1 64 10`, `tesskernel 3` † |
| DomainTex (integer, ccw, 15) | 1, 0, 15, 10, 1 | `tess 1 0 15 10`, `tesskernel 1` |
| QuadEven (frac_even, cw, none) | 3, 1, 64, 8, 1 | `tess 3 1 64 8`, `tesskernel 1` |
| MultiCB | 2, 1, 64, 10, **3** | `tess 2 1 64 10`, `tesskernel 3` |
| Instanced (both variants) | 2, 1, 64, 10, **2** | `tess 2 1 64 10`, `tesskernel 2` |
| FurShader (all 112 triples, our output only) | n/a | `tess 2 1 64 10`; `tesskernel` 3 / 4 / 5 / 6 (28 triples each) |

† **Built-in-RP artefact only.** On Metal, the built-in RP flattens Unity's built-in cbuffers into one `$Globals`
(VGlobals), while D3D keeps `UnityPerDraw`/`UnityPerCamera`/... as separate cbuffers. HDRP declares real cbuffers on
both platforms, which is why MultiCB/Instanced (and so the FurShader) match.

**Rule for the converter:** header[16..35] = the `tess` record's four values, then `tesskernel`. Both come from our
reflection (`xlat.cpp` already prints them).

## 3. MSL layout of the combined program

Unity concatenates the three separately translated stages under one prelude, with literal marker comments. Order:

1. **Prelude.** `#include <metal_stdlib>`, `#include <metal_texture>`, `using namespace metal;`.
2. **VS part:**
   - cbuffer `struct X_Type` declarations
   - `Mtl_VertexIn` (`[[ attribute(n) ]]`, the mesh inputs)
   - `Mtl_VertexOut` (`[[ user(SEM) ]]`)
   - `constant bool has_base_vertex_instance [[ function_constant(4) ]];`, **only** if the VS uses `SV_VertexID`/`SV_InstanceID`
   - `static Mtl_VertexOut vertexFunction(...)`, a plain function instead of an entry point
3. **`// SHADER_STAGE_HULL_begin`:**
   - `Mtl_ControlPoint` (device layout of one control point)
   - `Mtl_ControlPointIn` (the same members as `[[ attribute(n) ]]` for the post-tess `stage_in`)
   - `Mtl_PatchConstant` / `Mtl_PatchConstantIn`, only if there are non-factor patch constants
   - `Mtl_KernelPatchInfo { uint numPatches; ushort numControlPointsPerPatch; }`
   - HS-only cbuffer structs, `Mtl_HullIn { Mtl_VertexOut cp[N]; }`, helper functions
   - `kernel void patchKernel(...)`
   - then `// SHADER_STAGE_HULL_end`
4. **`// SHADER_STAGE_DOMAIN_begin`:**
   - DS-only cbuffer structs
   - `Mtl_VertexInPostTess { patch_control_point<Mtl_ControlPointIn> cp; [Mtl_PatchConstantIn patch;] }`
   - `Mtl_VertexOutPostTess` (`float4 mtl_Position [[ position, invariant ]]` + varyings)
   - helpers, `[[patch(...)]] vertex ... xlatMtlMain(...)`
   - then `// SHADER_STAGE_DOMAIN_end`

**Kernel** (Unity, `TessTest/MultiCB`): VS+HS resources first, then the tessellation plumbing from index K = header[32]:

```metal
kernel void patchKernel(
    constant GlobalsCB_Type& GlobalsCB [[ buffer(0) ]],
    constant PerDrawCB_Type& PerDrawCB [[ buffer(1) ]],
    constant PerMaterialCB_Type& PerMaterialCB [[ buffer(2) ]],
    sampler sampler_VSTex [[ sampler (0) ]],
    texture2d<float, access::sample > _VSTex [[ texture(0) ]] ,
    Mtl_VertexIn vertexInput [[ stage_in ]],
    uint2 tID [[ thread_position_in_grid ]],
    ushort2 groupID [[ threadgroup_position_in_grid ]],
    device Mtl_ControlPoint *controlPoints [[ buffer(3) ]],                 // K
    device MTLTriangleTessellationFactorsHalf *tessFactors [[ buffer(4) ]], // K+1 (K+2 if patchConstants)
    constant Mtl_KernelPatchInfo &patchInfo [[ buffer(5) ]])                // last
{
    Mtl_ControlPoint output;
    const uint numPatchesInThreadGroup = 10;
    const uint patchID = (tID.x / patchInfo.numControlPointsPerPatch);
    const bool patchValid = (patchID < patchInfo.numPatches);
    const uint mtl_BaseInstance = 0;
    const uint mtl_InstanceID = groupID.y - mtl_BaseInstance;
    const uint internalPatchID = mtl_InstanceID * patchInfo.numPatches + patchID;
    ...
    threadgroup Mtl_HullIn inputGroup[numPatchesInThreadGroup];
    threadgroup Mtl_HullIn &input = inputGroup[patchIDInThreadGroup];
    MTLTriangleTessellationFactorsHalf tessFactor;
    if (patchValid) {
        input.cp[controlPointID] = vertexFunction(GlobalsCB, PerDrawCB, PerMaterialCB, sampler_VSTex, _VSTex, vertexInput);
        output.INTERNALTESSPOS0 = input.cp[controlPointID].INTERNALTESSPOS0;   // pass-through control-point phase
        ...
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);
    if (!patchValid) { return; }
    // fork_phase2 { ... tessFactor.edgeTessellationFactor[i] = ...; tessFactor.insideTessellationFactor = ...; }
    controlPoints[mtl_VertexID] = output;
    tessFactors[internalPatchID] = tessFactor;
    // patchConstants[internalPatchID] = patch;   when there are patch constants
}
```

With patch constants (DomainTex), the kernel order is `controlPoints` K, `patchConstants` K+1, `tessFactors` K+2,
`patchInfo` K+3. Quad domains use `MTLQuadTessellationFactorsHalf` (`edgeTessellationFactor[4]`,
`insideTessellationFactor[2]`).

**Post-tessellation vertex function** (Unity, `TessTest/MultiCB`): the same VS+HS resources at the same slots, then
DS-only resources at the next free slots, then the plumbing again from D = **all** buffer slots allocated by VS+HS+DS:

```metal
[[patch(triangle, 3)]] vertex Mtl_VertexOutPostTess xlatMtlMain(
    constant GlobalsCB_Type& GlobalsCB [[ buffer(0) ]],
    constant PerDrawCB_Type& PerDrawCB [[ buffer(1) ]],
    constant PerMaterialCB_Type& PerMaterialCB [[ buffer(2) ]],
    sampler sampler_VSTex [[ sampler (0) ]],
    texture2d<float, access::sample > _VSTex [[ texture(0) ]] ,
    constant DomainCB_Type& DomainCB [[ buffer(3) ]],                        // DS-only: next free slot
    sampler sampler_HeightMap [[ sampler (1) ]],
    texture2d<float, access::sample > _HeightMap [[ texture(1) ]] ,
    float3 mtl_TessCoord [[ position_in_patch ]],                            // float2 for quad
    uint patchID [[ patch_id ]],
    const device Mtl_ControlPoint *controlPoints [[ buffer(4) ]],            // D (kernel had it at 3)
    const device MTLTriangleTessellationFactorsHalf *tessFactors [[ buffer(5) ]],
    Mtl_VertexInPostTess input [[ stage_in ]])
{
    Mtl_VertexOutPostTess output;
    MTLTriangleTessellationFactorsHalf tessFactor;
    tessFactor = tessFactors[patchID];          // always emitted; SV_InsideTessFactor -> tessFactor.insideTessellationFactor
    ... input.cp[k].MEMBER, input.patch.MEMBER, mtl_TessCoord ...
}
```

- **Always present:** Unity emits `patchID`, `controlPoints`, `[patchConstants]`, `tessFactors` and the `tessFactor`
  read even when unused (HDRPStyle, QuadEven). Each signature is wrapped in
  `#pragma clang diagnostic ignored "-Wunused-parameter"`.
- **Instance and vertex IDs:**
  - The kernel takes the instance from `groupID.y` and generates the vertex ID; base vertex and base instance are 0.
  - `vertexFunction` takes `uint mtl_InstanceID, uint mtl_BaseInstance, uint mtl_VertexID, uint mtl_BaseVertex` and
    subtracts the bases under `if(has_base_vertex_instance)`.
  - The kernel signature carries comments in place of those arguments (`// mtl_InstanceID passed through groupID,` …).
  - The domain gets `uint mtl_InstanceID [[ instance_id ]]` and
    `uint mtl_BaseInstance [[ base_instance, function_constant(has_base_vertex_instance) ]]`.
- **Function constants:** only `function_constant(4)` (`has_base_vertex_instance`), plus the fragment program's usual
  `rp_output_remap_mask` `function_constant(1)`. There are **no tessellation-specific function constants**.
- **Stage-in attribute numbering:** `Mtl_ControlPointIn`/`Mtl_PatchConstantIn` attributes reuse the VS input
  attribute number for the same semantic. For example, `NORMAL0` is 1 in both `Mtl_VertexIn` and
  `Mtl_ControlPointIn`. New semantics take the next numbers (`INTERNALTESSPOS0` → 3 or 4, then patch constants).
- **cbuffer structs:** Unity declares the **full** cbuffer layout in the tess path, unused members included. In
  MultiCB, `_ViewMatrix`, `_GlobalPad[4]` and `_MatPad` are never read but are declared. The normal path declares
  only used members.

**Not observable here:** how the runtime builds the post-tess vertex descriptor, which buffer index it gives the
`stage_in` control points, and the threadgroup size it dispatches. Running the test app needs the Mac's screen.
- Slot allocation is consistent with "controlPoints at D = buffer slots of the whole program" and
  "threadgroup = numPatchesInThreadGroup × CPs".
- **Matching Unity's slot numbers exactly** (which our HLSLcc already does) makes this moot.

## 4. Parameter tables

- **Combined program** (`progVertex[i]`): the parameter blob entry lists the cbuffer names and sizes (with the
  leading empty `''` cb) and **no values and no bindings**.
  - Everything is in `progVertex.m_CommonParameters` (`IsPartialCB=true`), as on D3D. Fragment programs work the same way.
- **Contents:**
  - Every cbuffer of all three stages, each with **all members** at Metal struct offsets.
  - `ConstantBufferBindings` with Index = `buffer()` slot (MultiCB: GlobalsCB 0, PerDrawCB 1, PerMaterialCB 2, DomainCB 3).
  - `TextureParams` with Index = `texture()` slot and SamplerIndex = `sampler()` slot for textures from any stage
    (MultiCB: `_VSTex` 0/0, `_HeightMap` 1/1; DomainTex: domain-only `_HeightMap` 0/0).
- **Sizes are unpadded** (end of the last member), e.g. `DomainCB` 20 (float4 + float), HDRPStyle `VGlobals` 2724.
  This is not tess-specific: normal Unity Metal programs do the same (`Hidden/ConvertTexture` VGlobals = 148).
- Our converter writes complete per-subprogram entries and empties `m_CommonParameters` (`MetalShader.Convert`).
  That stays valid here, so listing only the members we know is fine.
- **D3D side:** an ordinary per-stage table for each of VS/HS/DS. The same cbuffer can sit at different registers in
  different stages (MultiCB: `PerMaterialCB` is b2 in the VS but b1 in the HS and DS. FurShader: `UnityPerMaterial` is b2 in the VS and HS but **b1 in the DS**).

## 5. Vertex channels of the combined program

The channel list = every distinct `[[ attribute(n) ]]` input in the combined source whose semantic is a standard
vertex channel, **including control-point and patch-constant attributes**.
- **Semantics:** POSITION, NORMAL, TANGENT, COLOR, TEXCOORD0–7.
- **Order:** sorted by attribute.
- **Source:** Unity channel (0 pos, 1 normal, 2 tangent, 3 colour, 4+n texcoord n).
- **Target:** 13 + attribute.
- **srcMap:** OR of `1 << source`.
- **Skipped:** semantics that aren't vertex channels (`INTERNALTESSPOS`, `CUSTOM_INSTANCE_ID`, our bogus `SV_*TessFactor`).

| Shader | D3D VS | Metal combined |
|---|---|---|
| HDRPStyle | map 0x13 `0->0 1->1 4->5` | map 0x13 `0->13 1->14 4->15` |
| DomainTex | map 0x1b `0->0 1->1 4->5 3->3` | map **0x21b** `0->13 1->14 4->15 3->16` **`9->18`** (patch constant `TEXCOORD5`, attr 5) |
| MultiCB | map 0x13 `0->0 1->1 4->5` | map **0x33** `0->13 1->14 4->15` **`5->17`** (CP `TEXCOORD1`, attr 4) |
| Instanced | map 0x1 `0->0` | map **0x81** `0->13` **`7->16`** (CP `TEXCOORD3`, attr 3) |
| QuadEven | map 0x1 `0->0` | map 0x1 `0->13` |

- **Rule change:** `MetalShader.Sub` currently throws when an input has no D3D channel. For tessellated programs it
  must derive the source from the semantic, add it to srcMap, and skip non-channel semantics.

## 6. Our HLSLcc vs Unity

**How to drive it:**
- One `GLSLCrossDependencyData` shared across `TranslateHLSLFromMem` calls, in order **VS → HS → DS**, all with
  flags `0x2020080`.
- **VS output:** `vertexFunction` plus structs.
- **HS output:** the `// SHADER_STAGE_HULL` section.
- **DS output:** the domain section plus the `tess` reflection.
- Concatenate the three with the `// SHADER_STAGE_*_begin/end` lines.
- The HS and DS stages share the VS's function arguments, cbuffer structs and slot allocators **by name**. A cbuffer at
  a different D3D register in another stage still resolves to the first declaration's slot. That is exactly Unity's
  numbering, including MultiCB's domain-only cbuffer at slot 3 and textures 0/1.

**Diff against Unity, per shader** (Unity's MSL vs ours, after the union fix):

| | Kernel + vertexFunction | Post-tess function | Header values |
|---|---|---|---|
| HDRPStyle / DomainTex / QuadEven | identical except the cbuffer layout (built-in RP artefact †) | missing plumbing (below) | identical except † |
| MultiCB | **identical** apart from struct members | missing plumbing; reads `tessFactor.edgeTessellationFactor[3]` (wrong, undeclared) | identical |
| Instanced | identical apart from the function constant and `int`/`uint` | missing plumbing; wrong factor index; extra `.x` on a scalar `uint` CP member | identical |

**What differs, and the fix for each** (ranked):
1. **cbuffer struct members (compile error without the fix).**
   - Each cbuffer struct is emitted once, by the first stage that uses it (normally the VS), from that stage's RDEF.
   - A VS RDEF holding only the VS's used members leaves `PerMaterialCB._TessellationFactor` (used in the hull) undeclared.
   - **Fix:** every stage's desc must carry the union of members across VS/HS/DS per cbuffer name, each stage keeping
     its own registers.
   - `CbLayouts` (commit 78fe9ba: one layout per cbuffer per shader, D3D offsets kept) already does this, as long as
     hull and domain programs are fed in. `MetalShader` already loops over `progHull`/`progDomain`.
   - **Gap:** a "conflicted" cbuffer falls back to the program's own layout. For a triple, that fallback must be the
     union over the triple's three descs instead.
   - Verified in scratch with a per-triple union (`union.py`): HDRPStyle, DomainTex, QuadEven and
     **28/28 non-instanced FurShader triples compile**.
   - Unity declares the full cbuffer; we can't (D3D tables list used members only), and don't need to, because we
     write the parameter tables ourselves.
2. **Post-tess function plumbing (patch HLSLcc's domain path).**
   - Our 2020 HLSLcc never declares `uint patchID [[ patch_id ]]`, `controlPoints`, `patchConstants`, `tessFactors`
     or `tessFactor = tessFactors[patchID];`.
   - It maps a domain read of `SV_TessFactor[i]` / `SV_InsideTessFactor` to a bogus `stage_in` attribute and emits
     `tessFactor.edgeTessellationFactor[3]` for the inside factor and `[2]` for edge 1.
   - **Fix:** emit Unity's arguments with D = buffer slots after DS allocation. Map the factor reads to
     `edgeTessellationFactor[i]` / `insideTessellationFactor` (quad: `insideTessellationFactor[i]`).
   - FurShader domains don't read the factors, so this only matters for parity of buffer bindings.
3. **`has_base_vertex_instance` undeclared.** `patch_hlslcc.py` skips the
   `constant bool has_base_vertex_instance [[ function_constant(4) ]];` line under `HLSLCC_FLAG_METAL_TESSELLATION`,
   but the tess `vertexFunction` and the domain's `base_instance` argument still use it. Unity declares it right
   before `static Mtl_VertexOut vertexFunction(`. **Fix:** drop the `== 0` tessellation condition. This affects the
   instancing variants only.
4. **The `.x` on scalar `uint` control-point members, and `bitFieldInsert` defined in both the HS and the DS section.**
   - These remain in the 84 FurShader instancing variants after fix 3.
   - **Unity 2022.3.62f2's own output has both bugs:** `TessTest/Instanced` fails `mtlcheck` with
     `member reference base type 'threadgroup uint'` and `redefinition of 'bitFieldInsert'`.
   - These are DOTS/STEREO instancing variants the game shouldn't select on Mac. Parity says leave them, or fix them
     as an improvement if one is ever selected.
5. **Cosmetic and general (not tess-specific).**
   - Our prelude adds `#if !(__HAVE_FMA__) ... #endif`, which Unity's 2022.3 output doesn't have (fragment programs too).
   - A `uint` cbuffer member becomes `int` plus a `uint(...)` cast, because Unity's tables report uint as type 1.
   - Our cbuffer sizes are padded; Unity reports the unpadded end of the last member. All harmless.

## 7. Implementation (2026-10-07, in the working tree)

**Code:**
- `tools/xlat/xlat.cpp`: `lmc_xlat_unity_tess`, which translates VS → HS → DS through one
  `GLSLCrossDependencyData` and joins the stages with Unity's markers. Reflection comes per stage after `stage\t<n>`.
- `tools/lmc`:
  - `MetalShader.Convert` pairs `progVertex/progHull/progDomain[t][i]`, throwing on a keyword or count mismatch.
  - The triple becomes one gpu-23 program. Hull/domain lists are emptied to 0 tiers and `stageCounts` is recomputed (4 → 2).
  - `Params.WithCbsOf`: each stage's cbuffers carry the union of the three stages' values, on top of `CbLayouts`.
  - `MetalProgram`: header[16..35] from `tess`/`tesskernel`; vertex channels follow the semantic rule from section 5.
  - `MetalParams` dedupes the repeated `cb` records.
- **`patch_hlslcc.py` change 6:**
  - `has_base_vertex_instance` is declared right before `vertexFunction`.
  - The post-tess function gets `patch_id`, `controlPoints`/`patchConstants`/`tessFactors` and
    `tessFactor = tessFactors[patchID]`. Its slots are taken from the free list, because the hull's `0xffff-n`
    keys collide with a reserved UAV key: the FurShader's debug pass has a domain UAV at u1.
  - Domain tess-factor inputs are named by semantic. The patch-constant signature holds D3D_NAME values, which the old switch misread as `edge[2]`/`edge[3]`.
  - Scalar control-point inputs drop `.x` exactly where Unity's do (domain; hull with a control-point phase).

**Checks:**
- **Test shaders:** `TessTest/MultiCB` hull + domain are byte-identical to Unity's. `TessTest/Instanced` differs
  only by the fma prelude and `int`/`uint` (general).
- **FurShader, all six passes, 96 unique programs (112 slots), `mtlcheck`:**
  - plain: **24/24 ok**
  - STEREO_INSTANCING: 12 ok / 12 fail, **exactly the variants the hybrid's own Unity compile fails/passes**
  - DOTS_INSTANCING: 36 ok / 12 fail; the hybrid has no DOTS variants
  - The failures are only Unity's two bug classes: `.x` on a threadgroup `uint` in a pass-through hull, and
    `bitFieldInsert` defined in both stages.
- **Against the hybrid's FurShader** (Unity-compiled from the LCPort reconstruction), all 56 non-DOTS variants match:
  - header fields, flags (39 with stereo) and vertex channels are identical
  - MSL outside the cbuffer structs is identical for 20 variants
  - 32 differ only by Unity's `int(...)` around a `uint` global (general `uint`→`int` reporting)
  - the 4 pass-0.2 variants differ because the game's own pass 0.2 has a debug-display cbuffer + UAV in the domain
    that the reconstruction lacks
- **Non-FurShader regression:**
  - `Unlit/Texture|Hidden/HDRP/TemporalAA|Exposure`: 0 problems, output data and MSL byte-identical to HEAD.
  - Full `metalize`: 179 shaders + 116 compute shaders, 0 problems. Every object is byte-identical to HEAD's run
    except the FurShader (now converted) and the geometry blit (the working tree's separate `NoPrograms` change).

**Still open:** test in game on a furred enemy. That needs the Mac GUI.
