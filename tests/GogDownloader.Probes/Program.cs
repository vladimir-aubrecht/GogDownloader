using GogDownloader.Probes;

await ContentProbes.RunAsync();
LibraryProbes.Run();
GameVaultProbes.Run();
await InstallerProbes.RunAsync();
ProgressProbes.Run();
await StartScriptProbes.RunAsync();
await DownloadProbes.RunAsync();
await ArchiveSafetyProbes.RunAsync();
await DlcProbes.RunAsync();
await DepotOverlayProbes.RunAsync();
await InstallerCacheProbes.RunAsync();
await InstallerLifecycleProbes.RunAsync();
await ApiFailureProbes.RunAsync();
await LinkProbes.RunAsync();
await InstallerIdentityProbes.RunAsync();
await InstallerLimitProbes.RunAsync();
await StreamingProbes.RunAsync();
await StorageProbes.RunAsync();

return Probe.Report();
