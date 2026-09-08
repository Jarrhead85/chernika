using System.Globalization;
using System.Text;
using Chernika.Domain;
using Chernika.Domain.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chernika.Infrastructure.Reports;

/// <summary>Результат генерации PDF-бланка ИК: безопасное имя файла + байты.</summary>
public sealed record IndividualCardPdfFile(string FileName, byte[] Content);

/// <summary>
/// E1: компоновка печатного PDF-бланка индивидуальной карты (A4 landscape).
/// Источник данных — только E0 IndividualCardExportDto; live-чтения запрещены.
/// </summary>
public static class IndividualCardPdfComposer
{
    static IndividualCardPdfComposer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static IndividualCardPdfFile Compose(IndividualCardExportDto dto)
    {
        var fileName = BuildFileName(dto.Code, dto.Version);
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.1f, Unit.Centimetre);
                page.DefaultTextStyle(text => text.FontFamily("Arial").FontSize(8f));

                page.Header().PaddingBottom(2).AlignRight()
                    .Text($"Индивидуальная карта норм расхода ГСМ — {dto.Code} {dto.Version}")
                    .FontSize(7.5f).FontColor(Colors.Grey.Darken1);

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"ИК {dto.Code} {dto.Version}").FontSize(7.5f);
                    row.RelativeItem().AlignRight().Text(text =>
                    {
                        text.DefaultTextStyle(x => x.FontSize(7.5f));
                        text.Span("Страница ");
                        text.CurrentPageNumber();
                        text.Span(" из ");
                        text.TotalPages();
                    });
                });

                page.Content().Column(col =>
                {
                    ComposeTitleAndRequisites(col, dto);
                    ComposeWarnings(col, dto);
                    ComposeComposition(col, dto);
                    ComposeHKSources(col, dto);
                    ComposeCoefficients(col, dto);
                    ComposeNormsTable(col, dto);
                    ComposePrimaryMaterials(col, dto);
                    ComposeHistory(col, dto);
                    ComposeSignatures(col, dto);
                });
            });
        });

        return new IndividualCardPdfFile(fileName, document.GeneratePdf());
    }

    /// <summary>Имя файла из immutable Code/Version с заменой небезопасных символов.</summary>
    public static string BuildFileName(string code, string version)
    {
        var raw = $"{code}_{version}";
        var sb = new StringBuilder(raw.Length + 4);
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        sb.Append(".pdf");
        return sb.ToString();
    }

    // ── 4.1. Заголовок и реквизиты ────────────────────────────────────────

    private static void ComposeTitleAndRequisites(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().Text("Организация: ________________________________________________").FontSize(9);
        col.Item().Text($"Филиал: {dto.BranchName}").FontSize(9);

        col.Item().PaddingTop(8).AlignCenter().Text("ИНДИВИДУАЛЬНАЯ КАРТА").FontSize(15).Bold();
        col.Item().AlignCenter().Text("норм расхода горюче-смазочных материалов").FontSize(10).Bold();

        col.Item().PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1);
                columns.RelativeColumn(2);
                columns.RelativeColumn(1);
                columns.RelativeColumn(2);
            });

            RequisitesRow(table, "Номер ИК:", dto.Code, "Версия:", dto.Version);
            RequisitesRow(table, "Статус:", dto.StatusDisplay, "Уровень объекта:", dto.ObjectLevelDisplay);
            RequisitesRow(table, "Объект:", $"{dto.TargetObjectCode} — {dto.TargetObjectName}", null, null, 3);
            if (!string.IsNullOrEmpty(dto.TargetContext))
                RequisitesRow(table, "Контекст:", dto.TargetContext, null, null, 3);
            RequisitesRow(table,
                "Создана:", DateT(dto.CreatedAt),
                "Сформирована:", DateT(dto.FormedAt));
            RequisitesRow(table,
                "Архивирована:", DateT(dto.ArchivedAt),
                "Автор:", dto.CreatedByName ?? dto.CreatedByUserId);
        });
    }

    private static void RequisitesRow(
        TableDescriptor table, string label1, string? value1, string? label2, string? value2, byte span = 1)
    {
        table.Cell().Border(0.5f).Padding(3).Text(label1).FontSize(8).Bold();
        if (span == 1)
        {
            table.Cell().Border(0.5f).Padding(3).Text(value1 ?? string.Empty).FontSize(8);
            table.Cell().Border(0.5f).Padding(3).Text(label2 ?? string.Empty).FontSize(8).Bold();
            table.Cell().Border(0.5f).Padding(3).Text(value2 ?? string.Empty).FontSize(8);
        }
        else
        {
            table.Cell().ColumnSpan(span).Border(0.5f).Padding(3).Text(value1 ?? string.Empty).FontSize(8);
        }
    }

    // ── 4.2. Warning block ────────────────────────────────────────────────

    private static void ComposeWarnings(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        if (dto.Warnings.Count == 0)
            return;

        col.Item().PaddingTop(8).Border(1).BorderColor(Colors.Grey.Darken1).Padding(6).Column(warnings =>
        {
            warnings.Item().Text("ОСОБЫЕ ОТМЕТКИ").FontSize(9).Bold();
            foreach (var warning in dto.Warnings.OrderBy(w => w.SortOrder))
            {
                var isDraftNotice = warning.Code is "DraftNotice" or "HistoricalNotice";
                warnings.Item().Text(text =>
                {
                    text.Span("• ").FontSize(8);
                    var message = text.Span(warning.Message).FontSize(8);
                    if (isDraftNotice)
                        message.Bold();
                });
            }
        });
    }

    // ── 4.3. Версия конструктивного состава ───────────────────────────────

    private static void ComposeComposition(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(10).SectionTitle("Версия конструктивного состава");
        if (dto.Compositions.Count == 0)
        {
            col.Item().Text("Состав не зафиксирован.").FontSize(8);
            return;
        }

        foreach (var composition in dto.Compositions)
        {
            col.Item().PaddingTop(4).Text($"{composition.TargetObjectCode} — {composition.TargetObjectName}")
                .FontSize(9).Bold();
            col.Item().Text(text =>
            {
                text.Span($"Версия состава: {composition.SourceCompositionVersion}").FontSize(8);
                text.Span($"   Количество: {composition.TargetQuantity}").FontSize(8);
                text.Span($"   Дата утверждения: {Date(composition.SourceApprovedAt)}").FontSize(8);
            });

            col.Item().PaddingTop(2).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(0.7f);
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(0.7f);
                });

                table.Header(header =>
                {
                    HeaderCell(header, "Агрегат");
                    HeaderCell(header, "Количество");
                    HeaderCell(header, "Узлы");
                    HeaderCell(header, "Количество");
                });

                foreach (var aggregate in composition.Aggregates)
                {
                    table.Cell().Border(0.5f).Padding(3).Text($"{aggregate.Code} — {aggregate.Name}").FontSize(8);
                    table.Cell().Border(0.5f).Padding(3).Text(aggregate.Quantity.ToString(CultureInfo.InvariantCulture)).FontSize(8);
                    table.Cell().Border(0.5f).Padding(3).Column(column =>
                    {
                        foreach (var node in aggregate.Nodes)
                            column.Item().Text($"{node.Code} — {node.Name}").FontSize(8);
                    });
                    table.Cell().Border(0.5f).Padding(3).Column(column =>
                    {
                        foreach (var node in aggregate.Nodes)
                            column.Item().Text($"×{node.Quantity}").FontSize(8);
                    });
                }
            });
        }
    }

    // ── 4.4. Нормативные источники ХК ─────────────────────────────────────

    private static void ComposeHKSources(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(10).SectionTitle("Нормативные источники ХК");
        if (dto.HKSources.Count == 0)
        {
            col.Item().Text("Источники ХК не зафиксированы.").FontSize(8);
            return;
        }

        var byId = dto.HKSources.ToDictionary(h => h.SnapshotId);

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1);
                columns.RelativeColumn(1.8f);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(0.8f);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.RelativeColumn(0.9f);
            });

            table.Header(header =>
            {
                HeaderCell(header, "Уровень");
                HeaderCell(header, "Объект");
                HeaderCell(header, "ХК");
                HeaderCell(header, "Версия");
                HeaderCell(header, "Утверждена");
                HeaderCell(header, "Начало действия");
                HeaderCell(header, "Окончание действия");
                HeaderCell(header, "Готовность");
            });

            foreach (var source in dto.HKSources)
            {
                var depth = DepthOf(source, byId);

                table.Cell().Border(0.5f).Padding(3).Text(source.ObjectLevelDisplay).FontSize(7.5f);
                table.Cell().Border(0.5f).PaddingLeft(3 + depth * 8).PaddingTop(3).PaddingRight(3).PaddingBottom(3)
                    .Text($"{source.SourceObjectCode} — {source.SourceObjectName}").FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(source.HKCardCode).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(source.HKCardVersion).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(Date(source.ApprovedAt)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(Date(source.EffectiveDate)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(Date(source.ExpirationDate)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(source.IsComplete ? "Полная" : "Частичная").FontSize(7.5f);
            }
        });
    }

    private static int DepthOf(
        IndividualCardExportHKSourceDto source, IReadOnlyDictionary<Guid, IndividualCardExportHKSourceDto> byId)
    {
        var depth = 0;
        var parent = source.ParentHKSourceSnapshotId;
        while (parent is not null && byId.TryGetValue(parent.Value, out var item))
        {
            depth++;
            parent = item.ParentHKSourceSnapshotId;
        }
        return depth;
    }

    // ── 4.5. Применённые коэффициенты ─────────────────────────────────────

    private static void ComposeCoefficients(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(10).SectionTitle("Применённые коэффициенты");
        if (dto.Coefficients.Count == 0)
        {
            col.Item().Text("Коэффициенты не применялись.").FontSize(8);
        }
        else
        {
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(1.6f);
                    columns.RelativeColumn(0.8f);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                });

                table.Header(header =>
                {
                    HeaderCell(header, "Тип");
                    HeaderCell(header, "Наименование");
                    HeaderCell(header, "Значение");
                    HeaderCell(header, "Условия применения");
                    HeaderCell(header, "Нормативное основание");
                });

                foreach (var coefficient in dto.Coefficients)
                {
                    table.Cell().Border(0.5f).Padding(3).Text(coefficient.TypeName).FontSize(8);
                    table.Cell().Border(0.5f).Padding(3).Text(coefficient.Name).FontSize(8);
                    table.Cell().Border(0.5f).Padding(3).Text(F6(coefficient.Value)).FontSize(8);
                    table.Cell().Border(0.5f).Padding(3).Text(coefficient.ConditionDescription ?? string.Empty).FontSize(8);
                    table.Cell().Border(0.5f).Padding(3).Text(coefficient.NormativeBasis ?? string.Empty).FontSize(8);
                }
            });
        }

        col.Item().PaddingTop(4).Text($"Общий коэффициент: {F6(dto.TotalCoefficient)}").FontSize(9).Bold();
    }

    // ── 4.6. Нормы расхода ГСМ ────────────────────────────────────────────

    private static void ComposeNormsTable(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(10).SectionTitle("Нормы расхода ГСМ");
        if (dto.Rows.Count == 0)
        {
            col.Item().Text("Расчётные строки отсутствуют.").FontSize(8);
            return;
        }

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(0.4f);
                columns.RelativeColumn(1.5f);
                columns.RelativeColumn(0.5f);
                columns.RelativeColumn(1.3f);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1);
                columns.RelativeColumn(0.9f);
                columns.RelativeColumn(0.9f);
                columns.RelativeColumn(0.9f);
                columns.RelativeColumn(0.5f);
                columns.RelativeColumn(0.7f);
                columns.RelativeColumn(1.4f);
            });

            table.Header(header =>
            {
                HeaderCell(header, "№");
                HeaderCell(header, "Сборочная единица");
                HeaderCell(header, "Кол-во");
                HeaderCell(header, "Основные марки ГСМ");
                HeaderCell(header, "Дублирующие марки ГСМ");
                HeaderCell(header, "Резервные марки ГСМ");
                HeaderCell(header, "Зарубежные марки ГСМ");
                HeaderCell(header, "Источник ХК");
                HeaderCell(header, "Базовая норма");
                HeaderCell(header, "Коэффициент");
                HeaderCell(header, "Расчётная норма");
                HeaderCell(header, "Ед. изм.");
                HeaderCell(header, "Периодичность");
                HeaderCell(header, "Примечание");
            });

            foreach (var row in dto.Rows.OrderBy(r => r.SortOrder))
            {
                table.Cell().Border(0.5f).Padding(3).Text(row.SortOrder.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text($"{row.AssemblyUnitCode} — {row.AssemblyUnitName}").FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(row.AssemblyUnitQuantity.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).MaterialCell(row.PrimaryMaterials);
                table.Cell().Border(0.5f).Padding(3).MaterialCell(row.DuplicateMaterials);
                table.Cell().Border(0.5f).Padding(3).MaterialCell(row.ReserveMaterials);
                table.Cell().Border(0.5f).Padding(3).MaterialCell(row.ForeignMaterials);
                table.Cell().Border(0.5f).Padding(3).Text($"{row.SourceHKCardCode} {row.SourceHKCardVersion}").FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(F6(row.BaseVolume)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(F6(dto.TotalCoefficient)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(row.CalculatedVolume.ToString("F0", CultureInfo.InvariantCulture)).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(row.UnitOfMeasure).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Text(row.Periodicity ?? string.Empty).FontSize(7.5f);
                table.Cell().Border(0.5f).Padding(3).Column(notes =>
                {
                    if (!string.IsNullOrEmpty(row.Notes))
                        notes.Item().Text(row.Notes).FontSize(7);
                    notes.Item().Text(
                        $"Норма ХК: {F6(row.SourceVolume)}; узлы ×{row.NodeQuantity}; агрегаты ×{row.AggregateQuantity}; изделия ×{row.ProductQuantity}")
                        .FontSize(6.5f).FontColor(Colors.Grey.Darken2);
                });
            }
        });
    }

    private static void MaterialCell(this IContainer container, IReadOnlyList<IndividualCardExportMaterialDto> materials)
    {
        container.Column(column =>
        {
            foreach (var material in materials)
            {
                column.Item().Text(material.MaterialName).FontSize(7.5f);
                if (!string.IsNullOrEmpty(material.Gost))
                    column.Item().Text($"ГОСТ/ТУ: {material.Gost}").FontSize(6.5f).FontColor(Colors.Grey.Darken2);
            }
        });
    }

    // ── 4.7. Основные марки ГСМ ───────────────────────────────────────────

    private static void ComposePrimaryMaterials(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(10).SectionTitle("Основные марки ГСМ");
        if (dto.PrimaryMaterials.Count == 0)
        {
            col.Item().Text("Основные марки ГСМ не зафиксированы.").FontSize(8);
            return;
        }

        col.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(0.7f);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
            });

            table.Header(header =>
            {
                HeaderCell(header, "Марка ГСМ");
                HeaderCell(header, "ГОСТ/ТУ");
                HeaderCell(header, "Ед. изм.");
                HeaderCell(header, "Количество строк");
                HeaderCell(header, "Значение по марке");
            });

            foreach (var primary in dto.PrimaryMaterials)
            {
                table.Cell().Border(0.5f).Padding(3).Text(primary.MaterialName).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(primary.Gost ?? string.Empty).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(primary.UnitOfMeasure).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(primary.RowCount.ToString(CultureInfo.InvariantCulture)).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(F6(primary.Value)).FontSize(8);
            }
        });
    }

    // ── 4.8. История версий ───────────────────────────────────────────────

    private static void ComposeHistory(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(10).SectionTitle("История версий");
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1.4f);
            });

            table.Header(header =>
            {
                HeaderCell(header, "Версия");
                HeaderCell(header, "Статус");
                HeaderCell(header, "Создана");
                HeaderCell(header, "Сформирована");
                HeaderCell(header, "Архивирована");
            });

            foreach (var entry in dto.History)
            {
                table.Cell().Border(0.5f).Padding(3).Text(entry.Version).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(IndividualCardDisplay.Status(entry.Status)).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(DateT(entry.CreatedAt)).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(DateT(entry.FormedAt)).FontSize(8);
                table.Cell().Border(0.5f).Padding(3).Text(DateT(entry.ArchivedAt)).FontSize(8);
            }
        });
    }

    // ── 4.9. Подписи ──────────────────────────────────────────────────────

    private static void ComposeSignatures(ColumnDescriptor col, IndividualCardExportDto dto)
    {
        col.Item().PaddingTop(16).Text($"Разработал: ____________________ / {dto.CreatedByName ?? string.Empty} / __________")
            .FontSize(9);
        col.Item().PaddingTop(6).Text("Сформировал: __________________ / ____________________ / __________").FontSize(9);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static void SectionTitle(this IContainer container, string title) =>
        container.Column(section =>
        {
            section.Item().Text(title).FontSize(9.5f).Bold();
            section.Item().PaddingTop(1).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });

    private static void HeaderCell(TableCellDescriptor header, string title) =>
        header.Cell().Border(0.5f).Background(Colors.Grey.Lighten2).Padding(3).Text(title).FontSize(7.5f).Bold();

    private static string F6(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Date(DateTime? value) => value?.ToString("dd.MM.yyyy") ?? string.Empty;

    private static string DateT(DateTime? value) => value?.ToString("dd.MM.yyyy HH:mm") ?? string.Empty;
}
