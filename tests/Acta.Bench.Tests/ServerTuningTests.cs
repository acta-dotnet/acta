using Acta.Bench;
using Xunit;

namespace Acta.Bench.Tests;

public sealed class ServerTuningTests
{
    private static readonly RunPlan Plan = Protocol.Plan("quick", ["pg", "mssql"], 1729);

    [Theory]
    [InlineData(40L * 1024, 27308)]
    [InlineData(96L * 1024, 32768)]
    [InlineData(12L * 1024, 8192)]
    public void The_memory_cap_is_two_thirds_of_the_docker_vm_up_to_32_gb(long vmMb, int capMb)
    {
        Assert.Equal(capMb, ServerTuning.MemoryCapMb(Plan, vmMb * 1048576));
    }

    [Fact]
    public void A_docker_vm_too_small_for_an_8_gb_cap_refuses_to_start()
    {
        var refusal = Assert.ThrowsAny<InvalidOperationException>(() => ServerTuning.MemoryCapMb(Plan, 12000L * 1048576));
        Assert.Contains("8192 MB", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(6, 6)]
    [InlineData(32, 8)]
    public void Tempdb_gets_one_data_file_per_visible_cpu_between_four_and_eight(int cpus, int files)
    {
        Assert.Equal(files, ServerTuning.TempdbDataFiles(Plan, cpus));
    }

    [Fact]
    public void Postgres_sizes_its_memory_from_the_cap()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["shared_buffers"] = "8008MB",
                ["effective_cache_size"] = "24024MB",
                ["maintenance_work_mem"] = "1024MB",
                ["max_wal_size"] = "8192MB",
                ["max_connections"] = "2500",
            },
            ServerTuning.PostgresFlags(Plan, 32032)
        );
        Assert.Equal(8008, ServerTuning.ShmSizeMb(Plan, 32032));
        Assert.Equal(8192, ServerTuning.ShmSizeMb(Plan, 32768));
    }

    [Fact]
    public void Sql_server_leaves_fifteen_percent_of_the_cap_to_the_os_and_sizes_tempdb_uniformly()
    {
        var settings = ServerTuning.SqlServerSettings(Plan, 32032, 32);
        Assert.Equal("27227", settings["max_server_memory_mb"]);
        Assert.Equal("8", settings["max_degree_of_parallelism"]);
        Assert.Equal("50", settings["cost_threshold_for_parallelism"]);
        Assert.Equal("1", settings["optimize_for_ad_hoc_workloads"]);
        Assert.Equal("1", settings["memory_optimized_tempdb_metadata"]);
        Assert.Equal("on", settings["trace_flag_3979"]);
        Assert.Equal("8 x 1024 MB, growth 256 MB", settings["tempdb_data_files"]);
        Assert.Equal("1 x 1024 MB, growth 256 MB", settings["tempdb_log_files"]);
        Assert.Equal("1 x 4096 MB, growth 512 MB", ServerTuning.SqlServerDatabaseFiles(Plan)["database_data_files"]);
        Assert.Equal("1 x 2048 MB, growth 512 MB", ServerTuning.SqlServerDatabaseFiles(Plan)["database_log_files"]);
    }

    [Fact]
    public void Files_of_unequal_size_or_percent_growth_read_differently_from_uniform_files()
    {
        Assert.Equal(
            "1 x 8 MB, growth 10%; 3 x 1024 MB, growth 256 MB",
            ServerTuning.Files([(1024, 256, false), (8, 10, true), (1024, 256, false), (1024, 256, false)])
        );
    }

    [Fact]
    public void A_readback_that_differs_or_is_missing_is_named_and_extra_readback_is_ignored()
    {
        var requested = new Dictionary<string, string>
        {
            ["shared_buffers"] = "8008MB",
            ["max_connections"] = "500",
            ["autovacuum"] = "on",
        };
        var readback = new Dictionary<string, string>
        {
            ["shared_buffers"] = "128MB",
            ["max_connections"] = "500",
            ["container_image_digest"] = "sha256:0",
        };
        string[] expected = ["shared_buffers: requested 8008MB, read 128MB", "autovacuum: requested on, read nothing"];
        Assert.Equal(expected, ServerTuning.Mismatches(requested, readback));
        Assert.Empty(ServerTuning.Mismatches(requested, new Dictionary<string, string>(requested)));
    }

    [Fact]
    public void A_compose_server_run_is_a_valid_plan()
    {
        Protocol.Validate(Plan with { UseComposeServers = true });
    }
}
