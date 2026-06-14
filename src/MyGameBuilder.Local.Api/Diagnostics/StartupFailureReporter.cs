namespace MyGameBuilder.Local.Api.Diagnostics;

public static class StartupFailureReporter
{
    public static void Write(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var foreground = Console.ForegroundColor;
        try
        {
            Console.WriteLine();
            WriteLine("============================================================", ConsoleColor.DarkRed);
            WriteLine("MYGAMEBUILDER LOCAL COULD NOT START", ConsoleColor.Red);
            WriteLine("The app hit a startup error before it could finish opening.", ConsoleColor.Gray);
            Console.WriteLine();
            WriteLine(exception.Message, ConsoleColor.Yellow);

            if (exception.InnerException is not null)
            {
                WriteLine(exception.InnerException.Message, ConsoleColor.DarkYellow);
            }

            Console.WriteLine();
            WriteLine("Details for troubleshooting:", ConsoleColor.Gray);
            Console.WriteLine(exception);
            WriteLine("============================================================", ConsoleColor.DarkRed);

            if (ShouldPromptBeforeExit())
            {
                Console.WriteLine();
                Console.Write("Press any key to exit...");
                Console.ReadKey(intercept: true);
                Console.WriteLine();
            }
        }
        finally
        {
            Console.ForegroundColor = foreground;
        }
    }

    private static void WriteLine(string message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(message);
    }

    private static bool ShouldPromptBeforeExit()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        try
        {
            _ = Console.KeyAvailable;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
