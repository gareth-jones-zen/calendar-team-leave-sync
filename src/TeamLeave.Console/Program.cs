// team-leave.exe: every command, with output. Output is Unicode to a console and UTF-8 when redirected (see
// ConsoleText).
var output = WinBackground.ConsoleText.Out;
return TeamLeave.Cli.Run(args, new TeamLeave.CliHost
{
    Out = output,
    Err = WinBackground.ConsoleText.Error,
    LogEcho = output,
});
