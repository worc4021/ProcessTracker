# Changelog

## 0.1.0 (2026-11-21)


### Features

* **cli:** admin prepare-data/remove-data for the installer ([cab6bc6](https://github.com/worc4021/ProcessTracker/commit/cab6bc6a9cb40e0518573a90251e0c3d558d1873))
* **cli:** MCP registration, auto-upgrade task and upgrade script ([8625d55](https://github.com/worc4021/ProcessTracker/commit/8625d55cb71560c510b9ae0f35ffd8b6a3823861))
* **cli:** proctrack with session kill guard ([e728a65](https://github.com/worc4021/ProcessTracker/commit/e728a65163f4f6d532a72ed5b118917677fbd882))
* **core:** activity signals and tree roll-up ([541edab](https://github.com/worc4021/ProcessTracker/commit/541edab0c2bffeb7c575ca5b7f6315c93f2210f6))
* **core:** load assessment ([9a19e6b](https://github.com/worc4021/ProcessTracker/commit/9a19e6beeb53191a186f07306867c177f1b74698))
* **core:** models and sample delta computation ([796c512](https://github.com/worc4021/ProcessTracker/commit/796c512da11452580a22307f242720f023d7b438))
* **core:** ring buffer and versioned segment history ([e7f454d](https://github.com/worc4021/ProcessTracker/commit/e7f454d13acd6bb593c9b1d816e86f2724ec315e))
* **core:** session attribution rules ([4d2aacc](https://github.com/worc4021/ProcessTracker/commit/4d2aacc4653b4982e01a33f137fb924b320e07c3))
* **core:** sessions, transcript metadata, orphan rules ([e057d56](https://github.com/worc4021/ProcessTracker/commit/e057d566cdab41ac765f2409786f8ee1219cabb0))
* **core:** SQLite state store with additive migrations ([26f0ebf](https://github.com/worc4021/ProcessTracker/commit/26f0ebfb070395db0aeeb284000b9a45d2a3a881))
* **core:** tracker tick pipeline and restart continuity ([805042a](https://github.com/worc4021/ProcessTracker/commit/805042ac4153730efbb1e50594ed30d349a6d43f))
* **deploy:** updater with health-checked rollback and install scripts ([0aa37aa](https://github.com/worc4021/ProcessTracker/commit/0aa37aa4d06595367fbdcbe8254bd841f1c3ca46))
* **installer:** WiX MSI with service, data folder, PATH, event source, auto-upgrade task and MCP registration ([5797c2e](https://github.com/worc4021/ProcessTracker/commit/5797c2eb88dc7b0e1dbe771d393fbb8f40273cf2))
* **native:** NtQuerySystemInformation snapshot source ([9d77de1](https://github.com/worc4021/ProcessTracker/commit/9d77de1680829c4807e8fa5070d8395870e4ba65))
* **native:** PEB inspector with env allowlist ([ccdcc48](https://github.com/worc4021/ProcessTracker/commit/ccdcc48d7f780c9512092dbcb388f3006e68b153))
* read session transcripts from the session owner's profile ([76cb72b](https://github.com/worc4021/ProcessTracker/commit/76cb72bd5765c077d931836bc14003560df44ead))
* **service:** caller identity and stateless MCP endpoint ([9024686](https://github.com/worc4021/ProcessTracker/commit/9024686f11e49f234f5e153a9ee9e30736aaec93))
* **service:** host, collector loop, REST API ([b4aa3c2](https://github.com/worc4021/ProcessTracker/commit/b4aa3c2254525f31e9f4af9f70000e22ee268e34))
* **service:** read-only browser dashboard at / with /api/overview snapshot ([507d5a3](https://github.com/worc4021/ProcessTracker/commit/507d5a3783fd2ec0699435e360945e6a73f1edf7))
* show Claude Code session titles, project and branch ([cc36af0](https://github.com/worc4021/ProcessTracker/commit/cc36af0e693e095ad3a7af6194a9e65a763ea063))
* show desktop session titles, project and branch ([db18ef2](https://github.com/worc4021/ProcessTracker/commit/db18ef2d30f8d1c8886428c8942ce17c920af705))


### Bug Fixes

* **cli:** DELETE-access lock handle, child ACL reset, trust-checked purge ([2691234](https://github.com/worc4021/ProcessTracker/commit/26912343c45d1f5ae85809995af3bc46a682727d))
* **cli:** drop LogonType from the SYSTEM task XML (rejected by schtasks) ([1749f1a](https://github.com/worc4021/ProcessTracker/commit/1749f1af97b8a3afa2746f59ac73ee19c20678c8))
* **cli:** handle-based, secure-from-birth data folder setup; DACL-aware trust check ([5f7cc4f](https://github.com/worc4021/ProcessTracker/commit/5f7cc4f2ac5ffcfd42bb5a91d3e6638be571eab3))
* **cli:** harden auto-upgrade task registration and upgrade script ([3b4ee80](https://github.com/worc4021/ProcessTracker/commit/3b4ee801431f3c6675284649d769780cc37b6ad8))
* **cli:** ignore inherit-only CREATOR OWNER ACE in folder trust check ([3a51032](https://github.com/worc4021/ProcessTracker/commit/3a5103297e5229efc1b3efa14a6e30261e6c8d5b))
* **cli:** never adopt an untrusted data folder; require a drive-letter profile path ([f751fa5](https://github.com/worc4021/ProcessTracker/commit/f751fa5c6aef60a7e051157f9998678ca71f56c8))
* **cli:** non-DELETE inspect handle, best-effort folder lock, never move aside on sharing violation ([d503c4b](https://github.com/worc4021/ProcessTracker/commit/d503c4b55c71d5af370df8911ce0e2f837929b83))
* **cli:** self-attribute caller from env, re-verify create time before kill, harden API errors ([df8666e](https://github.com/worc4021/ProcessTracker/commit/df8666ea1aca97e34f3e6b16e48ef3d54dd9dccc))
* **core:** empty session id still anchors; skip injected transcript records ([71633b5](https://github.com/worc4021/ProcessTracker/commit/71633b520b32f759e123a44373d7e0bcca61f291))
* **core:** history writes idle processes as a heartbeat by activity thresholds ([afba97d](https://github.com/worc4021/ProcessTracker/commit/afba97da8f8b978c34dca8f4baa68db43902c0dc))
* **core:** leave counter-less processes out of a live-counter save ([6eb7b1d](https://github.com/worc4021/ProcessTracker/commit/6eb7b1db5e5731c48988fb595fb2310f8566955d))
* **core:** nested claude.exe no longer hijacks a desktop session's anchor ([4eb787d](https://github.com/worc4021/ProcessTracker/commit/4eb787de12b14d3c1da7b86fca236a2b08f89938))
* **core:** resumed sessions go live again; bound transcript read to 256 KB of bytes ([c6d1878](https://github.com/worc4021/ProcessTracker/commit/c6d18782d636e8bc2d024649387a84931049275b))
* **core:** tracker survives inspector and persistence failures ([d07cbdf](https://github.com/worc4021/ProcessTracker/commit/d07cbdf8660fda7fb389293ca6df9b84387cf1e0))
* **dashboard:** headers on every dashboard response, cached overview, UI polish ([4bce0a5](https://github.com/worc4021/ProcessTracker/commit/4bce0a5ea21aad334fc6e8265fe10e1c3221eadb))
* **deploy:** safer upgrades and install; MCP header default; perf tests out of deploy ([f3d88f0](https://github.com/worc4021/ProcessTracker/commit/f3d88f03d8bb36d73c89b3d25e1dae26c724ff4f))
* harden legacy uninstall and deploy native calls under PowerShell 5.1; docs ([04f0797](https://github.com/worc4021/ProcessTracker/commit/04f079729ab56892943d94a1f83b001e1143632b))
* nested claude.exe in a session no longer anchors a spurious terminal session ([de7e3b7](https://github.com/worc4021/ProcessTracker/commit/de7e3b74412b28ab1aef1edf2cf63d704fdb1b36))
* sanitize session text for terminals and harden desktop metadata reads ([0749683](https://github.com/worc4021/ProcessTracker/commit/0749683216250fae8c8fba54f3d81e4417ed0c47))
* **service:** DNS-rebinding guard and exact caller identity ([54c7ea4](https://github.com/worc4021/ProcessTracker/commit/54c7ea4678c146662f1d9cec62691103ca23f65f))
* **service:** keep listeners on loopback whatever the configuration says ([134cedf](https://github.com/worc4021/ProcessTracker/commit/134cedfe7b927611279543033df3347a344091c1))
* **service:** port from the command line only; Information to the event log ([9764da3](https://github.com/worc4021/ProcessTracker/commit/9764da33f6b32ec53d876b4da1db38cd42309302))
* transcript title strips leading injected tag blocks ([6b86fa1](https://github.com/worc4021/ProcessTracker/commit/6b86fa169de04f0db6908d46cc4036a5e85ec171))


### Performance Improvements

* **core:** one-pass activity roll-up and fewer per-tick allocations ([a5fdf60](https://github.com/worc4021/ProcessTracker/commit/a5fdf601a0ea07851b0a554b0fce543436ef86d9))
* **core:** save live counters every 30 s, reuse frame buffers, plain read-only view ([15c788e](https://github.com/worc4021/ProcessTracker/commit/15c788e16c480abe1abb9f8967be9568f2008017))
* **dashboard:** flat slim overview rows, lazy per-session detail via ?expand, bounded cache, JSON depth 256 ([0a43005](https://github.com/worc4021/ProcessTracker/commit/0a43005276dafa779bb36673d521cf4b4fcbc4c5))

## Changelog
