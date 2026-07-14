using GogDownloader.Probes;

await ContentProbes.RunAsync();
LibraryProbes.Run();
GameVaultProbes.Run();
await InstallerProbes.RunAsync();
await StartScriptProbes.RunAsync();
await DownloadProbes.RunAsync();

return Probe.Report();
