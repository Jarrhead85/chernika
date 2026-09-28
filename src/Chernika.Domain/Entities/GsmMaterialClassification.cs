namespace Chernika.Domain.Entities;

/// <summary>
/// Классификация марки ГСМ: одна группа и одна подгруппа в этой строке.
/// <para>
/// У марки может быть несколько строк (несколько подгрупп одной группы), но
/// нормализованная группа должна быть у всех строк одна и та же.
/// </para>
/// <para>
/// Переходный период: строка создаётся только при явно определённой группе.
/// Старый <see cref="GsmMaterial.Type"/> переносится сюда лишь вместе с группой
/// и остаётся переходным полем до PR-6.
/// </para>
/// </summary>
public class GsmMaterialClassification
{
    public Guid Id { get; set; }
    public Guid GsmMaterialId { get; set; }
    public GsmMaterial GsmMaterial { get; set; } = null!;
    public string GroupName { get; set; } = string.Empty;
    public string SubgroupName { get; set; } = string.Empty;
}
