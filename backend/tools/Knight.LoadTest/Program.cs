// The ingestion and delivery load test (TODO.md phase 10).
//
// The phase asks to "load-test ingestion and delivery; measure before adding a
// broker or TSDB". The order in that sentence is the point: the question is
// whether the plain PostgreSQL-and-EF path is fast enough, and it cannot be
// answered by reasoning about it. This tool answers it with numbers.
//
// It drives the real HTTP API — real handshake, real bearer token, real rate
// limiter, real EF write path — because that is the thing being measured. An
// in-process harness would measure a different program.
//
//   Seed the fixtures (writes directly through the domain services, since there
//   is no registration endpoint and the dashboard API requires MFA):
//     dotnet run --project tools/Knight.LoadTest -- seed --stores 25
//
//   Run the load:
//     dotnet run --project tools/Knight.LoadTest -- run \
//       --base-url http://localhost:5215 --duration 60 --concurrency 32
//
// `seed` needs CONTROL_PLANE_DB_CONNECTION_STRING. `run` needs only the URL and
// the fixture file that `seed` wrote.

using Knight.LoadTest;

return args.FirstOrDefault() switch
{
    "seed" => await Seeder.RunAsync(args),
    "run" => await Driver.RunAsync(args),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: Knight.LoadTest seed [--stores N] [--fixtures PATH]");
    Console.Error.WriteLine("       Knight.LoadTest run  [--base-url URL] [--duration SECONDS]");
    Console.Error.WriteLine("                            [--concurrency N] [--fixtures PATH]");
    return 1;
}
