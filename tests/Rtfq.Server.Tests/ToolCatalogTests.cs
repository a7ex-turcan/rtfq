using Rtfq.Mcp;

namespace Rtfq.Server.Tests;

/// <summary>
/// The MCP tool surface, checked against what the rest of the system tells an
/// agent it can do.
///
/// A field report found `describe_table`'s error and the `explore_source` prompt
/// both directing agents to `refresh`, a tool the runtime did not expose. That is
/// the "API must never lie about what it can do" failure CLAUDE.md names: an agent
/// plans from what discovery lists, so a promised-but-absent tool is a defect of
/// the same order as a broken one.
/// </summary>
public sealed class ToolCatalogTests
{
    static List<string> Names() =>
        [.. ToolCatalog.Describe().Select(t => t!["name"]!.GetValue<string>())];

    [Fact]
    public void Refresh_is_a_tool_an_agent_can_actually_call()
    {
        Assert.Contains("refresh", Names());
    }

    [Fact]
    public void The_discovery_and_write_tools_are_all_present()
    {
        // The full surface, stated once so a removal is loud. Nine before refresh
        // landed; ten now.
        string[] expected =
        [
            "list_sources", "describe_source", "describe_table", "sample",
            "query", "explain", "refresh", "propose_write", "commit_write", "abort_write",
        ];

        Assert.Equal(expected.OrderBy(x => x), Names().OrderBy(x => x));
    }

    [Fact]
    public void Every_tool_declares_a_name_and_a_description_and_an_input_schema()
    {
        foreach (var tool in ToolCatalog.Describe())
        {
            var t = Assert.IsType<System.Text.Json.Nodes.JsonObject>(tool);
            Assert.False(string.IsNullOrWhiteSpace(t["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(t["description"]!.GetValue<string>()));
            Assert.Equal("object", t["inputSchema"]!["type"]!.GetValue<string>());
        }
    }
}
