using ConsoleLiveWeb.Components;
using ConsoleLiveWeb.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("secrets.appsettings.json", optional: false, reloadOnChange: false);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient();
builder.Services.AddScoped<SpeechSynthesisService>();
builder.Services.AddScoped<VoiceLiveSignalingProxy>();
builder.Services.AddScoped<VoiceLiveAvatarProxy>();
builder.Services.AddSingleton<TextToVideoAvatarService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseWebSockets();
app.UseAntiforgery();

app.MapStaticAssets();
app.Map("/voice-live/signaling", async (HttpContext context, VoiceLiveSignalingProxy proxy) =>
{
    await proxy.HandleAsync(context);
});
app.Map("/voice-live-avatar/session", async (HttpContext context, VoiceLiveAvatarProxy proxy) =>
{
    await proxy.HandleAsync(context);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
