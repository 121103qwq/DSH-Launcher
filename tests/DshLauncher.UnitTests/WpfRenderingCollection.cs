using Xunit;

namespace DshLauncher.UnitTests;

// WPF's process-wide ContentPresenter/style caches may deadlock during first
// initialization on competing STA threads. Production has one UI dispatcher;
// keep presentation tests serial while pure service tests can remain parallel.
[CollectionDefinition("WpfRendering", DisableParallelization = true)]
public sealed class WpfRenderingCollection;
