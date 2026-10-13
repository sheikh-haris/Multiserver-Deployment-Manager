// Every test here changes the one applicationHost.config of this machine. Two at once fail with "the file has changed on disk".
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
