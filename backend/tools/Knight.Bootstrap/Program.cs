// Manual, explicit administrative bootstrap for the very first administrator.
// Deliberately NOT part of the API host — there is no public registration
// endpoint and there must never be one; see docs/security/README.md. Run this
// once, by hand, against the target database:
//
//   dotnet run --project tools/Knight.Bootstrap -- --email admin@example.com
//
// The password is never accepted as a command-line argument (that would leak
// into shell history) — it is always read interactively, masked, and confirmed.
//
// Phase 8 removed the legacy platform path this tool used to offer alongside the
// control plane one. `--control-plane` is still accepted so that runbooks and
// scripts written against the old tool keep working, but it no longer selects
// anything: the control plane is the only thing left to bootstrap.

var email = ParseEmailArgument(args);
if (email is null)
{
    Console.Error.WriteLine("Usage: Knight.Bootstrap --email <email>");
    return 1;
}

var password = ReadPasswordWithConfirmation();
if (password is null)
{
    Console.Error.WriteLine("Passwords did not match. Aborting.");
    return 1;
}

if (password.Length is < 10 or > 128)
{
    Console.Error.WriteLine("Password must be between 10 and 128 characters.");
    return 1;
}

return await Knight.Bootstrap.ControlPlaneBootstrap.RunAsync(email, password);

static string? ParseEmailArgument(string[] arguments)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (arguments[i] is "--email" or "-e")
        {
            return arguments[i + 1];
        }
    }

    return null;
}

static string? ReadPasswordWithConfirmation()
{
    var first = ReadHiddenLine("Password: ");
    var second = ReadHiddenLine("Confirm password: ");

    return string.Equals(first, second, StringComparison.Ordinal) ? first : null;
}

static string ReadHiddenLine(string prompt)
{
    Console.Write(prompt);

    // Console.ReadKey requires an interactive console; fall back to a plain
    // (unmasked) read when input is redirected — e.g. scripted/CI usage. The
    // masked interactive path remains the norm for a human running this by hand.
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? string.Empty;
    }

    var buffer = new System.Text.StringBuilder();

    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length > 0)
            {
                buffer.Length--;
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            buffer.Append(key.KeyChar);
        }
    }

    return buffer.ToString();
}
