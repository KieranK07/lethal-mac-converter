// Differential test, x86_64 under Rosetta: Valve's own SDK 1.48 libsteam_api vs our shim on every
// struct helper that needs no running Steam. Same inputs -> byte-identical structs and results.
// (Interface methods/accessors are covered by check_abi.py; ToString/ParseString need Steam.)
// Usage: difftest <valve 1.48 libsteam_api.dylib> <our libsteam_api.dylib>
#include <dlfcn.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

static void *hv, *ho;
static unsigned char bv[1024], bo[1024], av[1024], ao[1024];  // self / second struct, per side
static unsigned char *other;  // av while calling Valve's, ao while calling ours
static int n, fails;

static void *sym(void *h, const char *s) {
    void *p = dlsym(h, s);
    if (!p) { printf("missing %s\n", s); fails++; }
    return p;
}
static void reset(int byte) { memset(bv, byte, 1024); memset(bo, byte, 1024); memset(av, byte, 1024); memset(ao, byte, 1024); }
static void check(const char *what, bool same) {
    n++;
    if (!same || memcmp(bv, bo, 1024) || memcmp(av, ao, 1024)) { printf("DIFF %s\n", what); fails++; }
}
// pointer results: same offset into self, both NULL, or equal strings elsewhere (static buffers)
static bool same_ptr(const char *rv, const char *ro) {
    long dv = rv - (const char *)bv, d_o = ro - (const char *)bo;
    bool inv = rv && dv >= 0 && dv < 1024, ino = ro && d_o >= 0 && d_o < 1024;
    return (!rv && !ro) || (inv && ino && dv == d_o) || (rv && ro && !inv && !ino && !strcmp(rv, ro));
}

#define F(name, T, sig) typedef T (*fn) sig; fn fv = (fn)sym(hv, "SteamAPI_" #name), fo = (fn)sym(ho, "SteamAPI_" #name); if (fv && fo)
#define V(name, sig, ...) do { F(name, void, sig) { other = av; fv(bv, ##__VA_ARGS__); other = ao; fo(bo, ##__VA_ARGS__); check(#name, true); } } while (0)
#define R(name, T, sig, ...) do { F(name, T, sig) { other = av; T rv = fv(bv, ##__VA_ARGS__); other = ao; T ro = fo(bo, ##__VA_ARGS__); check(#name, !memcmp(&rv, &ro, sizeof(T))); } } while (0)
#define P(name, sig, ...) do { F(name, const char *, sig) { other = av; const char *rv = fv(bv, ##__VA_ARGS__); other = ao; const char *ro = fo(bo, ##__VA_ARGS__); check(#name, same_ptr(rv, ro)); } } while (0)

int main(int argc, char **argv) {
    if (argc != 3 || !(hv = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL)) || !(ho = dlopen(argv[2], RTLD_NOW | RTLD_LOCAL))) {
        printf("dlopen failed: %s\n", dlerror()); return 2;
    }
    const char *s31 = "0123456789012345678901234567890", *s32 = "01234567890123456789012345678901";
    const char *s33 = "012345678901234567890123456789012", *longname = "a server name that is clearly longer than the sixty-four byte buffer";
    static const uint8_t v6[16] = { 0x20, 0x01, 0x0d, 0xb8, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

    reset(0xAB);  // servernetadr_t
    V(servernetadr_t_Construct, (void *));
    V(servernetadr_t_Init, (void *, unsigned, uint16_t, uint16_t), 0x01020304u, 27016, 27015);
    R(servernetadr_t_GetQueryPort, uint16_t, (void *));
    V(servernetadr_t_SetQueryPort, (void *, uint16_t), 1234);
    R(servernetadr_t_GetConnectionPort, uint16_t, (void *));
    V(servernetadr_t_SetConnectionPort, (void *, uint16_t), 4321);
    R(servernetadr_t_GetIP, uint32_t, (void *));
    V(servernetadr_t_SetIP, (void *, uint32_t), 0x7f000001u);
    P(servernetadr_t_GetConnectionAddressString, (void *));
    P(servernetadr_t_GetQueryAddressString, (void *));
    R(servernetadr_t_IsLessThan, bool, (void *, void *), other);
    V(servernetadr_t_Assign, (void *, void *), other);
    R(servernetadr_t_IsLessThan, bool, (void *, void *), other);

    reset(0xAB);  // gameserveritem_t, MatchMakingKeyValuePair_t, SteamIPAddress_t
    V(gameserveritem_t_Construct, (void *));
    V(gameserveritem_t_SetName, (void *, const char *), longname);
    P(gameserveritem_t_GetName, (void *));
    V(gameserveritem_t_SetName, (void *, const char *), "");
    P(gameserveritem_t_GetName, (void *));
    reset(0xAB);
    V(MatchMakingKeyValuePair_t_Construct, (void *));
    R(SteamIPAddress_t_IsSet, bool, (void *));
    reset(0);
    R(SteamIPAddress_t_IsSet, bool, (void *));

    reset(0xAB);  // SteamNetworkingIPAddr
    V(SteamNetworkingIPAddr_Clear, (void *));
    R(SteamNetworkingIPAddr_IsIPv6AllZeros, bool, (void *));
    V(SteamNetworkingIPAddr_SetIPv6, (void *, const uint8_t *, uint16_t), v6, 27015);
    R(SteamNetworkingIPAddr_IsIPv4, bool, (void *));
    R(SteamNetworkingIPAddr_GetIPv4, uint32_t, (void *));
    R(SteamNetworkingIPAddr_IsIPv6AllZeros, bool, (void *));
    V(SteamNetworkingIPAddr_SetIPv4, (void *, uint32_t, uint16_t), 0x0a000001u, 7777);
    R(SteamNetworkingIPAddr_IsIPv4, bool, (void *));
    R(SteamNetworkingIPAddr_GetIPv4, uint32_t, (void *));
    R(SteamNetworkingIPAddr_IsLocalHost, bool, (void *));
    V(SteamNetworkingIPAddr_SetIPv4, (void *, uint32_t, uint16_t), 0x7f000001u, 7777);
    R(SteamNetworkingIPAddr_IsLocalHost, bool, (void *));
    R(SteamNetworkingIPAddr_IsEqualTo, bool, (void *, void *), other);
    memcpy(av, bv, 1024); memcpy(ao, bo, 1024);
    R(SteamNetworkingIPAddr_IsEqualTo, bool, (void *, void *), other);
    V(SteamNetworkingIPAddr_SetIPv6LocalHost, (void *, uint16_t), 1);
    R(SteamNetworkingIPAddr_IsLocalHost, bool, (void *));
    R(SteamNetworkingIPAddr_IsIPv6AllZeros, bool, (void *));

    reset(0xAB);  // SteamNetworkingIdentity
    V(SteamNetworkingIdentity_Clear, (void *));
    R(SteamNetworkingIdentity_IsInvalid, bool, (void *));
    V(SteamNetworkingIdentity_SetSteamID, (void *, uint64_t), 76561197960287930ull);
    R(SteamNetworkingIdentity_GetSteamID, uint64_t, (void *));
    R(SteamNetworkingIdentity_GetSteamID64, uint64_t, (void *));
    R(SteamNetworkingIdentity_IsInvalid, bool, (void *));
    V(SteamNetworkingIdentity_SetSteamID64, (void *, uint64_t), 123ull);
    R(SteamNetworkingIdentity_GetSteamID, uint64_t, (void *));
    P(SteamNetworkingIdentity_GetXboxPairwiseID, (void *));
    const char *strs[] = { "", "abc", s31, s32, s33 };
    for (int i = 0; i < 5; i++) {
        R(SteamNetworkingIdentity_SetXboxPairwiseID, bool, (void *, const char *), strs[i]);
        P(SteamNetworkingIdentity_GetXboxPairwiseID, (void *));
        R(SteamNetworkingIdentity_SetGenericString, bool, (void *, const char *), strs[i]);
        P(SteamNetworkingIdentity_GetGenericString, (void *));
        R(SteamNetworkingIdentity_SetGenericBytes, bool, (void *, const void *, uint32_t), s33, (uint32_t)strlen(strs[i]));
        P(SteamNetworkingIdentity_GetGenericBytes, (void *, int *), (int *)other);
    }
    V(SteamNetworkingIdentity_SetIPAddr, (void *, void *), other);
    P(SteamNetworkingIdentity_GetIPAddr, (void *));
    R(SteamNetworkingIdentity_IsLocalHost, bool, (void *));
    V(SteamNetworkingIdentity_SetLocalHost, (void *));
    R(SteamNetworkingIdentity_IsLocalHost, bool, (void *));
    P(SteamNetworkingIdentity_GetIPAddr, (void *));
    P(SteamNetworkingIdentity_GetGenericString, (void *));
    R(SteamNetworkingIdentity_IsEqualTo, bool, (void *, void *), other);
    memcpy(av, bv, 1024); memcpy(ao, bo, 1024);
    R(SteamNetworkingIdentity_IsEqualTo, bool, (void *, void *), other);

    reset(0xAB);  // SteamDatagramHostedAddress
    V(SteamDatagramHostedAddress_Clear, (void *));
    V(SteamDatagramHostedAddress_SetDevAddress, (void *, uint32_t, uint16_t, uint32_t), 0x0a000001u, 27015, 0x616d7300u);
    R(SteamDatagramHostedAddress_GetPopID, uint32_t, (void *));

    printf("difftest: %d struct-helper calls vs Valve 1.48, %d differ\n", n, fails);
    return fails ? 1 : 0;
}
