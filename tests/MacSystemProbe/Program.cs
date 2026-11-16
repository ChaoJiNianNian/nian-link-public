using NianLink.MacDiscoveryBoundary;

return MacProbe.Run(args, OperatingSystem.IsMacOS(), MacSystemEvidence.Read, Console.Out, Console.Error);
