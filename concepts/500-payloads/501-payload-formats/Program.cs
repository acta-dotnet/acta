using Acta;
using Acta.Concepts.PayloadFormats;
using Acta.Labs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new ConceptLab(builder.Configuration, args));

builder.Services.AddSingleton<IJobPayloadSerializer, ScalarV1Serializer>();
builder.Services.AddSingleton<IJobPayloadSerializer, MsgpackSerializer>();
builder.Services.AddSingleton<IJobPayloadSerializer, JsonGzipSerializer>();

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PayloadFormatsJobs>("payload-formats");
});
builder.Services.AddHostedService<Primer>();

await builder.Build().RunAsync();
