// How to choose a payload format for a job
// A job names the format its payload is stored in. This program runs custom formats side by side and compares their
// serialization time, enqueue time, readability, and stored bytes.
using Acta;
using Acta.Concepts.PayloadFormats;
using Acta.Labs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new ConceptLab(builder.Configuration, args));

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
