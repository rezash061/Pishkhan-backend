using Pishkhan.Web.Hosting;
using Pishkhan.Workers;

// One binary, deployed once per worker group (Workers:Groups), so each group scales on its own (backend plan 2.2).
var builder = WebApplication.CreateBuilder(args);
builder.AddWorkersHost();

var app = builder.Build();
app.UsePishkhanWebHost();
await app.RunAsync();
