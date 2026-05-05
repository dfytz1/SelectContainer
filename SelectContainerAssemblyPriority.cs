using Grasshopper.Kernel;

namespace SelectContainer;

public sealed class SelectContainerAssemblyPriority : GH_AssemblyPriority
{
	public override GH_LoadingInstruction PriorityLoad()
	{
		SelectContainerCanvasHook.Register();
		return GH_LoadingInstruction.Proceed;
	}
}
