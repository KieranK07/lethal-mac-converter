"""Bring the public HLSLcc (2020) Metal output in line with what Unity 2022.3's player expects.

Usage: python3 -I patch_hlslcc.py <copy of HLSLcc source tree>

Each change mirrors what Unity 2022.3's own Metal shaders contain:
1. Colour outputs use the render-pass remap function constant:
   `rp_output_remap_mask [[ function_constant(1) ]]`, 4 bits per output.
   This replaces the old `xlt_remap_o` array.
2. Vertex positions are `[[ position, invariant ]]`. HDRP's depth prepass followed by ZTest Equal relies on this.
3. Base vertex/instance are subtracted only when the player sets `has_base_vertex_instance`
   (`function_constant(4)`).
"""
import pathlib
import sys

root = pathlib.Path(sys.argv[1]) / "src"


def patch(rel, old, new, count=1):
    p = root / rel
    s = p.read_text()
    n = s.count(old)
    if n != count:
        sys.exit(f"{rel}: expected {count} match(es), found {n}: {old[:60]!r}")
    p.write_text(s.replace(old, new))


# 1. render-pass output remap
patch("internal_includes/toMetal.h", "    bool m_NeedFBOutputRemapDecl;", "    bool m_NeedFBOutputRemapDecl;\n    uint32_t m_FBOutputsUsed = 0;")
patch("toMetalDeclaration.cpp",
      'oss << type << " " << name << " [[ color(xlt_remap_o[" << psSignature->ui32SemanticIndex << "]) ]]";',
      'oss << type << " " << name << " [[ color(rp_output_remap_" << psSignature->ui32SemanticIndex << ") ]]";\n'
      '                    m_FBOutputsUsed |= 1u << psSignature->ui32SemanticIndex;')
patch("toMetal.cpp",
      '            bcatcstr(glsl, "#ifndef XLT_REMAP_O\\n\\t#define XLT_REMAP_O {0, 1, 2, 3, 4, 5, 6, 7}\\n#endif\\nconstexpr constant uint xlt_remap_o[] = XLT_REMAP_O;\\n");',
      '        {\n'
      '            bcatcstr(glsl, "constant uint32_t rp_output_remap_mask [[ function_constant(1) ]];\\n");\n'
      '            for (int i = 0; i < 8; i++)\n'
      '                if (m_FBOutputsUsed & (1u << i))\n'
      '                    bformata(glsl, "constant const uint rp_output_remap_%d = (rp_output_remap_mask >> %d) & 0xF;\\n", i, 4 * i);\n'
      '        }')

# 2. invariant position
patch("toMetalDeclaration.cpp", '"float4 mtl_Position [[ position ]]"', '"float4 mtl_Position [[ position, invariant ]]"', 2)
patch("toMetalDeclaration.cpp", 'oss << " [[ position ]]";', 'oss << " [[ position, invariant ]]";')

# 3. base vertex / instance behind function constant 4
patch("toMetalDeclaration.cpp", '"uint mtl_BaseInstance [[ base_instance ]]"', '"uint mtl_BaseInstance [[ base_instance, function_constant(has_base_vertex_instance) ]]"')
patch("toMetalDeclaration.cpp", '"uint mtl_BaseVertex [[ base_vertex ]]"', '"uint mtl_BaseVertex [[ base_vertex, function_constant(has_base_vertex_instance) ]]"')
for what, base in (("InstanceID", "BaseInstance"), ("VertexID", "BaseVertex")):
    patch("toMetal.cpp",
          f'                    bcatcstr(bodyglsl, "#if !UNITY_SUPPORT_INDIRECT_BUFFERS\\n");\n'
          f'                    psContext->AddIndentation();\n'
          f'                    bcatcstr(bodyglsl, "mtl_{base} = 0;\\n");\n'
          f'                    bcatcstr(bodyglsl, "#endif\\n");\n'
          f'                    psContext->AddIndentation();\n'
          f'                    bcatcstr(bodyglsl, "mtl_{what} = mtl_{what} - mtl_{base};\\n");',
          f'                    bcatcstr(bodyglsl, "    if(has_base_vertex_instance)\\n");\n'
          f'                    psContext->AddIndentation();\n'
          f'                    bcatcstr(bodyglsl, "    mtl_{what} = mtl_{what} - mtl_{base};\\n");')
# the function constant itself, declared once the vertex shader's builtin inputs are known
patch("toMetal.cpp",
      "        DeclareClipPlanes(&psShader->asPhases[0].psDecl[0], psShader->asPhases[0].psDecl.size());",
      "        if (psShader->eShaderType == VERTEX_SHADER && (psContext->flags & HLSLCC_FLAG_METAL_TESSELLATION) == 0)\n"
      "            for (const auto &mem : m_StructDefinitions[\"\"].m_Members)\n"
      "                if (mem.first == \"mtl_BaseVertex\" || mem.first == \"mtl_BaseInstance\")\n"
      "                {\n"
      "                    bcatcstr(glsl, \"constant bool has_base_vertex_instance [[ function_constant(4) ]];\\n\");\n"
      "                    break;\n"
      "                }\n"
      "        DeclareClipPlanes(&psShader->asPhases[0].psDecl[0], psShader->asPhases[0].psDecl.size());")
print("HLSLcc patched for Unity 2022.3 Metal conventions")
