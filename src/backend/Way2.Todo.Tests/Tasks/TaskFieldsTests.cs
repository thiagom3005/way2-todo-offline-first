using Way2.Todo.Api.Tasks;

namespace Way2.Todo.Tests.Tasks;

public sealed class TaskFieldsTests
{
    private static TaskFields Valid() =>
        new("Conferir medição", "Unidade A", new DateOnly(2026, 10, 30), TaskPriority.High, TodoTaskStatus.Pending, "Campo");

    [Fact]
    public void Normalize_trims_text_and_turns_blank_description_into_null()
    {
        var fields = (Valid() with { Title = "  Título  ", Description = "   ", Category = " Casa " }).Normalize();

        Assert.Equal("Título", fields.Title);
        Assert.Null(fields.Description);
        Assert.Equal("Casa", fields.Category);
    }

    [Fact]
    public void Valid_fields_have_no_errors()
    {
        Assert.Empty(Valid().Normalize().Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_is_required(string? title)
    {
        var errors = (Valid() with { Title = title }).Normalize().Validate();

        Assert.Contains("title", errors.Keys);
    }

    [Fact]
    public void Text_limits_are_enforced()
    {
        var errors = (Valid() with
        {
            Title = new string('t', TaskFields.TitleMaxLength + 1),
            Description = new string('d', TaskFields.DescriptionMaxLength + 1),
            Category = new string('c', TaskFields.CategoryMaxLength + 1),
        }).Normalize().Validate();

        Assert.Equal(["category", "description", "title"], errors.Keys.Order());
    }

    [Fact]
    public void Limits_are_inclusive()
    {
        var errors = (Valid() with
        {
            Title = new string('t', TaskFields.TitleMaxLength),
            Description = new string('d', TaskFields.DescriptionMaxLength),
            Category = new string('c', TaskFields.CategoryMaxLength),
        }).Normalize().Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Category_is_required()
    {
        var errors = (Valid() with { Category = " " }).Normalize().Validate();

        Assert.Contains("category", errors.Keys);
    }

    [Fact]
    public void Undefined_enum_values_are_rejected()
    {
        var errors = (Valid() with { Priority = (TaskPriority)42, Status = (TodoTaskStatus)42 }).Normalize().Validate();

        Assert.Contains("priority", errors.Keys);
        Assert.Contains("status", errors.Keys);
    }
}
