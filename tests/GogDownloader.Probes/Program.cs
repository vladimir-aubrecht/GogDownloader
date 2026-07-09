using GogDownloader.Probes;

await ContentProbes.RunAsync();
LibraryProbes.Run();
GameVaultProbes.Run();
await DownloadProbes.RunAsync();

return Probe.Report();
