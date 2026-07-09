using System.Runtime.CompilerServices;

// The zip and publish steps have no public surface but need direct exercise: they are the
// difference between a portable backup and a half-written archive.
[assembly: InternalsVisibleTo("GogDownloader.Probes")]
