using GogDownloader.Probes;

await ContentProbes.RunAsync();
LibraryProbes.Run();
GameVaultProbes.Run();
await InstallerProbes.RunAsync();
await DownloadProbes.RunAsync();

return Probe.Report();
