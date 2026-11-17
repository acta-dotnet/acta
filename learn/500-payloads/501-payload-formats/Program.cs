// How to choose a payload format for a job
// A job names the format its payload is stored in: JSON by default, or a format of your own. This program adds two
// custom formats, `scalar-v1` and `json-gzip`, and runs the same echo job through each.
using Acta;
using Acta.Labs;
using Acta.Learn.PayloadFormats;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new LearnLab(builder.Configuration, args));

// learn: 2. Register a serializer for each format
// A custom format is an `IJobPayloadSerializer` in the application's container.
builder.Services.AddSingleton<IJobPayloadSerializer, ScalarV1Serializer>();
builder.Services.AddSingleton<IJobPayloadSerializer, JsonGzipSerializer>();

// learn: end

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<PayloadFormatsJobs>("payload-formats");
});
builder.Services.AddHostedService<Primer>();

await builder.Build().RunAsync();
