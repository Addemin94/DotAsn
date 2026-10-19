using Asn1.Studio;
using Asn1.Studio.Engine;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// One workbench per browser tab: it holds the compiled schema shared by all tools.
builder.Services.AddSingleton<Workbench>();
builder.Services.AddSingleton<LazyAssemblyLoader>();

await builder.Build().RunAsync();
