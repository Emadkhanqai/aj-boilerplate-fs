using AjBoilerplate.Application.Common;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Domain.Items;

namespace AjBoilerplate.UnitTests.Items;

/// <summary>SAMPLE SLICE. The FluentValidation rules that produce the 400s.</summary>
public sealed class ItemValidatorTests
{
    private static readonly byte[] AnyToken = Guid.NewGuid().ToByteArray();

    [Fact]
    public void Create_accepts_a_minimal_valid_command() =>
        Assert.True(new CreateItemCommandValidator()
            .Validate(new CreateItemCommand("Widget", null, null)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string name) =>
        Assert.False(new CreateItemCommandValidator()
            .Validate(new CreateItemCommand(name, null, null)).IsValid);

    [Fact]
    public void Create_rejects_a_name_over_the_limit() =>
        Assert.False(new CreateItemCommandValidator()
            .Validate(new CreateItemCommand(new string('x', Item.MaxNameLength + 1), null, null)).IsValid);

    [Fact]
    public void Create_rejects_an_unknown_status()
    {
        var result = new CreateItemCommandValidator().Validate(new CreateItemCommand("Widget", null, "Deleted"));

        Assert.False(result.IsValid);
        // The message must name the acceptable values, or a client integrator has to read the source.
        Assert.Contains("Draft", result.Errors[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("active")]
    [InlineData("ARCHIVED")]
    public void Create_accepts_a_known_status_in_any_casing(string status) =>
        Assert.True(new CreateItemCommandValidator()
            .Validate(new CreateItemCommand("Widget", null, status)).IsValid);

    [Fact]
    public void Create_rejects_a_numeric_status() =>
        // "1" parses to an enum member but is not a value a client should ever send; accepting it
        // would silently couple the API to the enum's declaration order.
        Assert.False(new CreateItemCommandValidator()
            .Validate(new CreateItemCommand("Widget", null, "1")).IsValid);

    [Fact]
    public void Update_accepts_a_valid_command() =>
        Assert.True(new UpdateItemCommandValidator()
            .Validate(new UpdateItemCommand(Guid.NewGuid(), "Widget", null, "Active", AnyToken)).IsValid);

    [Fact]
    public void Update_requires_an_id() =>
        Assert.False(new UpdateItemCommandValidator()
            .Validate(new UpdateItemCommand(Guid.Empty, "Widget", null, "Active", AnyToken)).IsValid);

    [Fact]
    public void Update_requires_a_status() =>
        Assert.False(new UpdateItemCommandValidator()
            .Validate(new UpdateItemCommand(Guid.NewGuid(), "Widget", null, "", AnyToken)).IsValid);

    [Fact]
    public void Update_requires_a_non_empty_row_version() =>
        // A missing token must be a 400, never a 409 — telling a user "someone else edited this"
        // when their client simply failed to send the token is a genuinely misleading error.
        Assert.False(new UpdateItemCommandValidator()
            .Validate(new UpdateItemCommand(Guid.NewGuid(), "Widget", null, "Active", [])).IsValid);

    [Fact]
    public void List_accepts_the_defaults() =>
        Assert.True(new ListItemsQueryValidator()
            .Validate(new ListItemsQuery(Paging.DefaultPage, Paging.DefaultPageSize, null)).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void List_rejects_a_non_positive_page(int page) =>
        Assert.False(new ListItemsQueryValidator()
            .Validate(new ListItemsQuery(page, Paging.DefaultPageSize, null)).IsValid);

    [Fact]
    public void List_rejects_a_page_size_over_the_maximum() =>
        // The bound is what stops a caller from asking for the entire table in one request.
        Assert.False(new ListItemsQueryValidator()
            .Validate(new ListItemsQuery(1, Paging.MaxPageSize + 1, null)).IsValid);

    [Fact]
    public void List_accepts_the_maximum_page_size() =>
        Assert.True(new ListItemsQueryValidator()
            .Validate(new ListItemsQuery(1, Paging.MaxPageSize, null)).IsValid);
}
