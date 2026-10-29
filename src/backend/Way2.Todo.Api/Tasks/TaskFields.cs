namespace Way2.Todo.Api.Tasks;

/// <summary>
/// Campos editáveis de uma tarefa (payload de um upsert). Não contém id, versão nem datas do servidor.
/// </summary>
public sealed record TaskFields(
    string? Title,
    string? Description,
    DateOnly? DueDate,
    TaskPriority Priority,
    TodoTaskStatus Status,
    string? Category)
{
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int CategoryMaxLength = 50;

    /// <summary>Remove espaços nas bordas; descrição vazia vira null.</summary>
    public TaskFields Normalize() => this with
    {
        Title = Title?.Trim() ?? string.Empty,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Category = Category?.Trim() ?? string.Empty,
    };

    /// <summary>Valida campos já normalizados. Chaves seguem os nomes JSON do contrato.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrEmpty(Title))
            errors["title"] = ["Título é obrigatório."];
        else if (Title.Length > TitleMaxLength)
            errors["title"] = [$"Título deve ter no máximo {TitleMaxLength} caracteres."];

        if (Description is { Length: > DescriptionMaxLength })
            errors["description"] = [$"Descrição deve ter no máximo {DescriptionMaxLength} caracteres."];

        if (string.IsNullOrEmpty(Category))
            errors["category"] = ["Categoria é obrigatória."];
        else if (Category.Length > CategoryMaxLength)
            errors["category"] = [$"Categoria deve ter no máximo {CategoryMaxLength} caracteres."];

        // U+0000 é JSON válido, mas o PostgreSQL não armazena: recusa na validação, por operação.
        foreach (var (key, value) in new[] { ("title", Title), ("description", Description), ("category", Category) })
        {
            if (value?.Contains('\0') == true && !errors.ContainsKey(key))
                errors[key] = ["Texto não pode conter o caractere nulo (U+0000)."];
        }

        if (!Enum.IsDefined(Priority))
            errors["priority"] = ["Prioridade inválida."];

        if (!Enum.IsDefined(Status))
            errors["status"] = ["Status inválido."];

        return errors;
    }
}
