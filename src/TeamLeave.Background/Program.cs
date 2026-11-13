// team-leave-background.exe: no window, ever. What the scheduled task runs (`--config SETTINGS sync --hook
// --scheduled`); anything else runs as the console program would, with nothing shown.
return TeamLeave.Cli.Run(args, new TeamLeave.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
