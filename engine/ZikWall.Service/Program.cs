using ZikWall.WinUI.Services;

// SCM is the only entry point; running the executable does not install anything.
if (args.Length == 0) WindowsServiceHost.Run(ServiceProtocol.Name, SessionService.RunAsync);
