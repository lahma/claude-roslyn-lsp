// The whole entry point: argv dispatch lives in CliDispatcher (D3) so that it stays testable.
return await ClaudeRoslynLsp.Cli.CliDispatcher.RunAsync(args).ConfigureAwait(false);
