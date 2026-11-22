// calendar-team-leave-sync.exe: every command, with output. Output is Unicode to a console and UTF-8 when redirected (see
// ConsoleText).
var output = WinBackground.ConsoleText.Out;
using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));   // digest's email
return TeamLeave.Cli.Run(args, new TeamLeave.CliHost
{
    In = input,
    Out = output,
    Err = WinBackground.ConsoleText.Error,
    LogEcho = output,
});
