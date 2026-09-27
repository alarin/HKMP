# HKMP.NetStress

Headless stress test of the HKMP networking layer. It runs a real `NetServer` and 2-3 real `NetClient`s in one
process, each client connected through an in-process UDP proxy that can drop, duplicate, reorder and delay
datagrams. Clients and server exchange reliable save updates (each tagged with a sequence number and checksum)
and unreliable entity updates at the normal send rate, with periodic bursts that make update packets exceed the
1200-byte MTU in both directions.

At the end it reports per direction how many reliable items were sent, received, missing, duplicated or corrupt,
plus parse failures on each side (from the log), timeouts and proxy statistics, and exits with 0 on PASS.

Build (the HKMP build to test is taken from `..\HKMP\bin\Release\net472\HKMP.dll`, override with
`-p:HkmpDll=...`):

    dotnet build HKMP.NetStress/HKMP.NetStress.csproj -c Release

Copy `UnityEngine.dll` and `UnityEngine.CoreModule.dll` next to the executable (NetClient touches a
MonoBehaviour type) and run it on Windows / .NET Framework:

    HKMP.NetStress.exe --clients 2 --loss 0.01 --duration 20 --drain 10 --burst-every 10

Options: `--clients`, `--duration`/`--drain` (seconds), `--loss`, `--dup`, `--reorder` with `--reorder-delay`,
`--delay` with `--jitter` (ms), `--items`, `--burst-every`, `--burst-items`, `--item-size`, `--entities`,
`--seed`, `--port` (proxies use the following ports), `--simultaneous-connect`, `--faults-during-connect`,
`--cpu-stress` (number of busy threads during traffic), `--reconnect` (halfway, disconnect and reconnect every
client with the same NetClient),
`--name`, `--log-dir`. Faults are injected after all clients connected unless `--faults-during-connect` is given.
