namespace Rtfq.Spike;

public static class Program
{
    public static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0] : "probe";
        return cmd switch
        {
            "probe" => Run(Probe.Run),
            "tsql"  => TSqlSpike.Run(),
            "pg"    => PgSpike.Run(),
            "anomaly" => Anomaly.Run(),
            _       => Fail($"unknown command: {cmd}")
        };
    }

    static int Run(Action a) { a(); return 0; }

    static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 2;
    }
}
