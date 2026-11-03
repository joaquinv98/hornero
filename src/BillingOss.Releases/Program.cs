using BillingOss.Releases;

// billing-oss-versiones: el vigía, el manifiesto y el canal (D-20). Ver Cli.Usage.
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
return await Cli.Run(args, http, Console.Out, TimeProvider.System, stop.Token);
