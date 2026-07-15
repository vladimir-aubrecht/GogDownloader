using GogDownloader.Probes;

await ContentProbes.RunAsync();
LibraryProbes.Run();
GameVaultProbes.Run();
await InstallerProbes.RunAsync();
ProgressProbes.Run();
await StartScriptProbes.RunAsync();
await DownloadProbes.RunAsync();

return Probe.Report();
