using BAP.WebCore;
using Microsoft.AspNetCore.Components;
using System.Reflection;

namespace WebTest
{
    public partial class App
    {
        [Inject]
        LoadedAddonHolder loadedAddons { get; set; } = default!;


    }
}
