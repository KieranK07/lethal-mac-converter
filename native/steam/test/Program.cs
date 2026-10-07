// Proves, without Steam running, that Facepunch 2.3.2 binds natively on arm64 to the SDK 1.48 shim:
// every P/Invoke resolves, into the shim itself except the re-exported core functions, and
// Steam-free 1.48 code paths (struct helpers, an accessor, a core re-export) behave.
// Run: DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec dotnet run -c Release
// Live (Steam running + logged in, owns Lethal Company): same command plus `-- --live`.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

Check(RuntimeInformation.ProcessArchitecture == Architecture.Arm64, $"process arch {RuntimeInformation.ProcessArchitecture}");

// 1. .NET's own binding of every DllImport (loads "libsteam_api" from the app dir, resolves the entry point).
var asm = typeof(SteamClient).Assembly;
var pinvokes = asm.GetTypes().SelectMany(t => t.GetMethods(Any)).Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl)).ToList();
var unbound = pinvokes.Where(m => { try { Marshal.Prelink(m); return false; } catch { return true; } }).ToList();
Check(pinvokes.Count == 928 && unbound.Count == 0, $"Marshal.Prelink: {pinvokes.Count - unbound.Count}/{pinvokes.Count} DllImports bound {string.Join(" ", unbound.Select(m => m.Name))}");

// 2. Which image each entry point lands in. Only the re-exported core functions may resolve into the core.
var dir = AppContext.BaseDirectory;
var shim = NativeLibrary.Load(Path.Combine(dir, "libsteam_api.dylib"));
var expectCore = File.ReadAllLines(Path.Combine(dir, "reexports.txt")).Select(s => s.TrimStart('_')).ToHashSet();
var names = pinvokes.Select(m => m.GetCustomAttribute<DllImportAttribute>().EntryPoint ?? m.Name).Distinct().ToList();
var inCore = names.Where(n => Native.ImageOf(NativeLibrary.GetExport(shim, n)).EndsWith("/libsteam_api_core.dylib")).ToHashSet();
var inShim = names.Count(n => Native.ImageOf(NativeLibrary.GetExport(shim, n)).EndsWith("/libsteam_api.dylib"));
Check(inCore.SetEquals(expectCore) && inShim + inCore.Count == names.Count, $"{names.Count} entry points: {inShim} in shim, {inCore.Count} re-exported from core");

// 3. Steam-free 1.48 code paths.
NetIdentity sid = (SteamId)76561197960287930UL;
Check(sid.IsSteamId && sid.SteamId.Value == 76561197960287930UL, "NetIdentity Set/GetSteamID (uint64_steamid <-> CSteamID)");
var lh = NetIdentity.LocalHost;
Check(lh.IsIpAddress && lh.Address.IsLocalHost && !lh.Address.IsIPv4, "NetIdentity SetLocalHost / GetIPAddr / IPAddr IsLocalHost");
var a = NetAddress.From("1.2.3.4", 27015);
Check(a.IsIPv4 && a.Address.ToString() == "1.2.3.4" && a.Port == 27015, "NetAddress SetIPv4 / IsIPv4 / GetIPv4");
Check(NetAddress.AnyIp(7777).IsIPv6AllZeros && !NetAddress.LocalHost(7777).IsIPv6AllZeros, "NetAddress SetIPv6 / SetIPv6LocalHost / IsIPv6AllZeros");

object Call(Type t, string m, params object[] xs) => t.GetMethod(m, Any).Invoke(null, xs);
string Utf8(object p) => Marshal.PtrToStringUTF8((IntPtr)p.GetType().GetField("ptr", Any).GetValue(p));
object id = default(NetIdentity);
var nt = typeof(NetIdentity);
var xs = new[] { id, "xbox-pairwise-id" };
Check((bool)Call(nt, "InternalSetXboxPairwiseID", xs) && (int)nt.GetField("type", Any).GetValue(xs[0]) == 17
      && Utf8(Call(nt, "InternalGetXboxPairwiseID", xs[0])) == "xbox-pairwise-id", "NetIdentity Set/GetXboxPairwiseID (hand-written, type 17)");
xs = new[] { id, "generic" };
Check((bool)Call(nt, "InternalSetGenericString", xs) && Utf8(Call(nt, "InternalGetGenericString", xs[0])) == "generic"
      && (bool)Call(nt, "InternalIsEqualTo", xs[0], xs[0]), "NetIdentity Set/GetGenericString / IsEqualTo");

var user = (IntPtr)Call(asm.GetType("Steamworks.ISteamUser"), "SteamAPI_SteamUser_v020");
Check(user == IntPtr.Zero, "accessor SteamAPI_SteamUser_v020 before init -> null (core ContextInit)");
var pipe = Call(asm.GetType("Steamworks.SteamAPI"), "GetHSteamPipe");
Check(pipe.ToString() == "0", $"re-exported SteamAPI_GetHSteamPipe before init -> {pipe}");

if (args.Contains("--live"))
{
    SteamClient.Init(1966720);  // SteamAPI_Init: the adapted path (newer core's SteamAPI_InitFlat)
    Check(SteamClient.IsLoggedOn, $"logged in as {SteamClient.Name} ({SteamClient.SteamId})");
    // the 20 interfaces SteamClient.Init installs; a null here = Facepunch crashes on first use
    foreach (var n in ("Apps_v008 Friends_v017 Input_v001 Inventory_v003 Matchmaking_v009 MatchmakingServers_v002 Music_v001 Networking_v006 " +
                       "NetworkingSockets_v008 NetworkingUtils_v003 ParentalSettings_v001 Parties_v002 RemoteStorage_v014 Screenshots_v003 " +
                       "UGC_v014 User_v020 UserStats_v011 Utils_v009 Video_v002 RemotePlay_v001").Split(' '))
        Check((IntPtr)pinvokes.First(m => m.Name == "SteamAPI_Steam" + n).Invoke(null, null) != IntPtr.Zero, $"steamclient serves 1.48 interface SteamAPI_Steam{n}");
    Check(SteamFriends.GetFriends().Any(), $"SteamFriends017: {SteamFriends.GetFriends().Count()} friends");
    Check(SteamUtils.IpCountry?.Length == 2, $"SteamUtils009 IpCountry {SteamUtils.IpCountry}");
    var ticket = SteamUser.GetAuthSessionTicket();
    Check(ticket?.Data?.Length > 0, $"SteamUser020 GetAuthSessionTicket {ticket?.Data?.Length} bytes");
    ticket?.Cancel();
    var lobbies = await SteamMatchmaking.LobbyList.FilterDistanceWorldwide().WithMaxResults(50).RequestAsync() ?? Array.Empty<Lobby>();
    Check(lobbies.Length > 0, $"SteamMatchMaking009 lobbies: {lobbies.Length}, e.g. " + string.Join(" | ", lobbies.Take(3).Select(l => $"{l.GetData("name")} v{l.GetData("vers")} {l.MemberCount}/{l.MaxMembers}")));
    SteamNetworkingUtils.InitRelayNetworkAccess();
    await Task.WhenAny(SteamNetworkingUtils.WaitForPingDataAsync(), Task.Delay(30000));
    Check(SteamNetworkingUtils.LocalPingLocation.HasValue, $"SteamNetworkingUtils003 relay access, status {SteamNetworkingUtils.Status}");
    SteamClient.Shutdown();
}

Console.WriteLine(fails == 0 ? "PASS" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] struct DlInfo { public IntPtr fname, fbase, sname, saddr; }
    [DllImport("libSystem.dylib")] static extern int dladdr(IntPtr addr, out DlInfo info);
    public static string ImageOf(IntPtr p) => dladdr(p, out var i) != 0 ? Marshal.PtrToStringUTF8(i.fname) : "?";
}
