using System.Globalization;
using System.Text;
using Chernika.Domain;
using Chernika.Domain.Models;
using ClosedXML.Excel;

namespace Chernika.Infrastructure.Reports;

/// <summary>Результат генерации XLSX-бланка ИК: безопасное имя файла + байты.</summary>
public sealed record IndividualCardXlsxFile(string FileName, byte[] Content);

/// <summary>Безопасные имена файлов отчётов из immutable Code/Version.</summary>
public static class ReportFileNameBuilder
{
    public static string Build(string code, string version, string extension)
    {
        var raw = $"{code}_{version}";
        var sb = new StringBuilder(raw.Length + extension.Length);
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        sb.Append(extension);
        return sb.ToString();
    }
}

/// <summary>
/// E2: XLSX-бланк ИК (один лист «ИК»). Источник данных — только E0
/// IndividualCardExportDto. Формулы позволяют локально пересчитывать нормы;
/// изменения в файле никогда не возвращаются в систему.
/// </summary>
public static class IndividualCardXlsxComposer
{
    private const int LastColumn = 18; // A..R
    private const string GramFormat = "0";
    private const string CoefficientFormat = "0.00";

    public static IndividualCardXlsxFile Compose(IndividualCardExportDto dto)
    {
        var fileName = ReportFileNameBuilder.Build(dto.Code, dto.Version, ".xlsx");

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("ИК");

        // wb.Style — общий изменяемый объект: стили строим на ячейках-донорах скрытой строки.
        const int StyleRow = 200;
        ws.Row(StyleRow).Hide();
        ws.Style.Font.SetFontName("Arial");
        ws.Style.Font.SetFontSize(9.0);
        ws.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);

        var titleStyle = ws.Cell(StyleRow, 1).Style;
        titleStyle.Font.SetFontSize(15.0);
        titleStyle.Font.SetBold();
        titleStyle.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        var sectionStyle = ws.Cell(StyleRow, 2).Style;
        sectionStyle.Font.SetFontSize(11.0);
        sectionStyle.Font.SetBold();

        var headerStyle = ws.Cell(StyleRow, 3).Style;
        headerStyle.Font.SetFontSize(9.0);
        headerStyle.Font.SetBold();
        headerStyle.Fill.SetBackgroundColor(XLColor.FromHtml("#E4EAF2"));
        headerStyle.Alignment.SetWrapText(true);
        headerStyle.Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        var labelStyle = ws.Cell(StyleRow, 4).Style;
        labelStyle.Font.SetBold();
        labelStyle.Alignment.SetWrapText(true);

        var textStyle = ws.Cell(StyleRow, 5).Style;
        textStyle.Alignment.SetWrapText(true);

        var editableStyle = ws.Cell(StyleRow, 6).Style;
        editableStyle.Fill.SetBackgroundColor(XLColor.FromHtml("#FFF3BF"));
        editableStyle.Alignment.SetWrapText(true);
        editableStyle.Protection.SetLocked(false);

        var warnStyle = ws.Cell(StyleRow, 7).Style;
        warnStyle.Font.SetBold();
        warnStyle.Font.SetFontColor(XLColor.FromHtml("#A04000"));

        var gramStyle = ws.Cell(StyleRow, 8).Style;
        gramStyle.Alignment.SetWrapText(true);
        gramStyle.NumberFormat.SetFormat(GramFormat);

        var coefficientStyle = ws.Cell(StyleRow, 9).Style;
        coefficientStyle.NumberFormat.SetFormat(CoefficientFormat);

        var editableCoefficientStyle = ws.Cell(StyleRow, 10).Style;
        editableCoefficientStyle.Fill.SetBackgroundColor(XLColor.FromHtml("#FFF3BF"));
        editableCoefficientStyle.Alignment.SetWrapText(true);
        editableCoefficientStyle.Protection.SetLocked(false);
        editableCoefficientStyle.NumberFormat.SetFormat(CoefficientFormat);

        int r = 1;

        ws.Cell(r, 1).Style = labelStyle;
        ws.Cell(r, 1).Value = "Организация: _________________________________________________";
        r++;
        ws.Cell(r, 1).Style = labelStyle;
        ws.Cell(r, 1).Value = $"Филиал: {dto.BranchName}";
        r += 1;

        ws.Range(r, 1, r, LastColumn).Merge();
        ws.Cell(r, 1).Style = titleStyle;
        ws.Cell(r, 1).Value = "ИНДИВИДУАЛЬНАЯ КАРТА";
        r++;
        ws.Range(r, 1, r, LastColumn).Merge();
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "норм расхода горюче-смазочных материалов";
        r += 2;

        void Req(string label, string value)
        {
            ws.Cell(r, 1).Style = labelStyle;
            ws.Cell(r, 1).Value = label;
            ws.Range(r, 2, r, 5).Merge();
            ws.Cell(r, 2).Style = textStyle;
            ws.Cell(r, 2).Value = value;
            r++;
        }

        Req("Номер ИК:", dto.Code);
        Req("Версия:", dto.Version);
        Req("Статус:", dto.StatusDisplay);
        Req("Уровень объекта:", dto.ObjectLevelDisplay);
        Req("Объект:", $"{dto.TargetObjectCode} — {dto.TargetObjectName}");
        if (!string.IsNullOrEmpty(dto.TargetContext))
            Req("Контекст:", dto.TargetContext);
        Req("Создана:", D(dto.CreatedAt));
        if (dto.FormedAt is { } formedAt)
            Req("Сформирована:", D(formedAt));
        if (dto.ArchivedAt is { } archivedAt)
            Req("Архивирована:", D(archivedAt));
        Req("Автор:", dto.CreatedByName ?? dto.CreatedByUserId);
        r++;

        if (dto.Warnings.Count > 0)
        {
            ws.Cell(r, 1).Style = sectionStyle;
            ws.Cell(r, 1).Value = "ОСОБЫЕ ОТМЕТКИ";
            r++;
            foreach (var warning in dto.Warnings.OrderBy(w => w.SortOrder))
            {
                ws.Range(r, 1, r, LastColumn).Merge();
                var c = ws.Cell(r, 1);
                c.Value = "• " + warning.Message;
                c.Style = warning.Code is "DraftNotice" or "HistoricalNotice" ? warnStyle : textStyle;
                r++;
            }
            r++;
        }

        // ── Версия конструктивного состава ────────────────────────────────
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "Версия конструктивного состава";
        r++;
        if (dto.Compositions.Count == 0)
        {
            ws.Cell(r, 1).Value = "Состав не зафиксирован.";
            r++;
        }
        foreach (var composition in dto.Compositions)
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Cell(r, 1).Style = labelStyle;
            ws.Cell(r, 1).Value = $"{composition.TargetObjectCode} — {composition.TargetObjectName}";
            r++;
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Cell(r, 1).Style = textStyle;
            ws.Cell(r, 1).Value =
                $"Версия состава: {composition.SourceCompositionVersion}   Количество: {composition.TargetQuantity}   Дата утверждения: {D(composition.SourceApprovedAt)}";
            r++;
            foreach (var aggregate in composition.Aggregates)
            {
                ws.Range(r, 2, r, LastColumn).Merge();
                ws.Cell(r, 2).Style = textStyle;
                ws.Cell(r, 2).Value = $"Агрегат: {aggregate.Code} — {aggregate.Name} ×{aggregate.Quantity}";
                r++;
                foreach (var node in aggregate.Nodes)
                {
                    ws.Range(r, 3, r, LastColumn).Merge();
                    ws.Cell(r, 3).Style = textStyle;
                    ws.Cell(r, 3).Value = $"Узел: {node.Code} — {node.Name} ×{node.Quantity}";
                    r++;
                }
            }
            r++;
        }

        // ── Нормативные источники ХК ──────────────────────────────────────
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "Нормативные источники ХК";
        r++;
        if (dto.HKSources.Count > 0)
        {
            string[] sourceHeaders = ["Уровень", "Объект", "ХК", "Версия", "Утверждена", "Начало действия", "Окончание действия", "Готовность"];
            for (var i = 0; i < sourceHeaders.Length; i++)
            {
                ws.Cell(r, i + 1).Style = headerStyle;
                ws.Cell(r, i + 1).Value = sourceHeaders[i];
            }
            r++;

            var byId = dto.HKSources.ToDictionary(h => h.SnapshotId);
            foreach (var source in dto.HKSources)
            {
                var depth = 0;
                var parent = source.ParentHKSourceSnapshotId;
                while (parent is not null && byId.TryGetValue(parent.Value, out var item))
                {
                    depth++;
                    parent = item.ParentHKSourceSnapshotId;
                }

                ws.Cell(r, 1).Style = textStyle;
                ws.Cell(r, 1).Value = source.ObjectLevelDisplay;
                ws.Cell(r, 2).Style = textStyle;
                ws.Cell(r, 2).Value = new string(' ', depth * 3) + $"{source.SourceObjectCode} — {source.SourceObjectName}";
                ws.Cell(r, 3).Style = textStyle;
                ws.Cell(r, 3).Value = source.HKCardCode;
                ws.Cell(r, 4).Style = textStyle;
                ws.Cell(r, 4).Value = source.HKCardVersion;
                ws.Cell(r, 5).Style = textStyle;
                ws.Cell(r, 5).Value = D(source.ApprovedAt);
                ws.Cell(r, 6).Style = textStyle;
                ws.Cell(r, 6).Value = D(source.EffectiveDate);
                ws.Cell(r, 7).Style = textStyle;
                ws.Cell(r, 7).Value = D(source.ExpirationDate);
                ws.Cell(r, 8).Style = textStyle;
                ws.Cell(r, 8).Value = source.IsComplete ? "Полная" : "Частичная";
                r++;
            }
        }
        else
        {
            ws.Cell(r, 1).Value = "Источники ХК не зафиксированы.";
            r++;
        }
        r++;

        // ── Применённые коэффициенты ──────────────────────────────────────
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "Применённые коэффициенты";
        r++;

        ws.Range(r, 1, r, LastColumn).Merge();
        ws.Cell(r, 1).Style = textStyle;
        ws.Cell(r, 1).Value = "Ячейки с выделенной заливкой допускают локальное изменение для пересчёта в Excel. " +
                              "Изменения не вносятся в ИК в системе.";
        r += 2;

        var coefficientFirstRow = r;
        if (dto.Coefficients.Count > 0)
        {
            string[] coefficientHeaders = ["Тип", "Наименование", "Значение", "Условия применения", "Нормативное основание"];
            int[] coefficientColumns = [1, 2, 4, 5, 7];
            for (var i = 0; i < coefficientHeaders.Length; i++)
            {
                ws.Cell(r, coefficientColumns[i]).Style = headerStyle;
                ws.Cell(r, coefficientColumns[i]).Value = coefficientHeaders[i];
            }
            r++;

            foreach (var coefficient in dto.Coefficients)
            {
                ws.Cell(r, 1).Style = textStyle;
                ws.Cell(r, 1).Value = coefficient.TypeName;
                ws.Cell(r, 2).Style = textStyle;
                ws.Cell(r, 2).Value = coefficient.Name;
                var valueCell = ws.Cell(r, 4);
                valueCell.Value = coefficient.Value;
                valueCell.Style = editableCoefficientStyle;
                ws.Cell(r, 5).Style = textStyle;
                ws.Cell(r, 5).Value = coefficient.ConditionDescription ?? string.Empty;
                ws.Cell(r, 7).Style = textStyle;
                ws.Cell(r, 7).Value = coefficient.NormativeBasis ?? string.Empty;
                r++;
            }
            var coefficientLastRow = r - 1;

            ws.Cell(r, 3).Style = labelStyle;
            ws.Cell(r, 3).Value = "Общий коэффициент:";
            var totalCell = ws.Cell(r, 4);
            totalCell.FormulaA1 = $"=PRODUCT(D{coefficientFirstRow + 1}:D{coefficientLastRow})";
            totalCell.Style = coefficientStyle;
            var totalCoefficientRow = r;
            r += 2;
            ComposeNorms(ws, dto, r, sectionStyle, headerStyle, textStyle, labelStyle, editableStyle, gramStyle, coefficientStyle, totalCoefficientRow);
        }
        else
        {
            ws.Cell(r, 1).Value = "Коэффициенты не применялись.";
            r++;
            ws.Cell(r, 3).Style = labelStyle;
            ws.Cell(r, 3).Value = "Общий коэффициент:";
            var totalCell = ws.Cell(r, 4);
            totalCell.Value = 1m;
            totalCell.Style = coefficientStyle;
            var totalCoefficientRow = r;
            r += 2;
            ComposeNorms(ws, dto, r, sectionStyle, headerStyle, textStyle, labelStyle, editableStyle, gramStyle, coefficientStyle, totalCoefficientRow);
        }

        return new IndividualCardXlsxFile(fileName, Save(wb));
    }

    private static void ComposeNorms(
        IXLWorksheet ws, IndividualCardExportDto dto, int r,
        IXLStyle sectionStyle, IXLStyle headerStyle, IXLStyle textStyle, IXLStyle labelStyle, IXLStyle editableStyle,
        IXLStyle gramStyle, IXLStyle coefficientStyle, int totalCoefficientRow)
    {
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "Нормы расхода ГСМ";
        r++;

        string[] headers =
        [
            "№", "Сборочная единица", "Кол-во", "Основные марки ГСМ", "Дублирующие марки ГСМ",
            "Резервные марки ГСМ", "Зарубежные марки ГСМ", "Источник ХК", "Норма ХК",
            "Кол-во узлов", "Кол-во агрегатов", "Кол-во изделий", "Базовая норма", "Коэффициент",
            "Расчётная норма", "Ед. изм.", "Периодичность", "Примечание",
        ];
        var headerRow = r;
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(r, i + 1).Style = headerStyle;
            ws.Cell(r, i + 1).Value = headers[i];
        }
        r++;

        var firstDataRow = r;
        foreach (var row in dto.Rows.OrderBy(x => x.SortOrder))
        {
            var excelRow = r;
            ws.Cell(excelRow, 1).Style = textStyle;
            ws.Cell(excelRow, 1).Value = row.SortOrder;
            ws.Cell(excelRow, 2).Style = textStyle;
            ws.Cell(excelRow, 2).Value = $"{row.AssemblyUnitCode} — {row.AssemblyUnitName}";

            ws.Cell(excelRow, 3).Style = editableStyle;
            ws.Cell(excelRow, 3).Value = row.AssemblyUnitQuantity;
            ws.Cell(excelRow, 4).Style = textStyle;
            ws.Cell(excelRow, 4).Value = MaterialText(row.PrimaryMaterials);
            ws.Cell(excelRow, 5).Style = textStyle;
            ws.Cell(excelRow, 5).Value = MaterialText(row.DuplicateMaterials);
            ws.Cell(excelRow, 6).Style = textStyle;
            ws.Cell(excelRow, 6).Value = MaterialText(row.ReserveMaterials);
            ws.Cell(excelRow, 7).Style = textStyle;
            ws.Cell(excelRow, 7).Value = MaterialText(row.ForeignMaterials);
            ws.Cell(excelRow, 8).Style = textStyle;
            ws.Cell(excelRow, 8).Value = $"{row.SourceHKCardCode} {row.SourceHKCardVersion}";
            ws.Cell(excelRow, 9).Style = gramStyle;
            ws.Cell(excelRow, 9).Value = row.SourceVolume;
            ws.Cell(excelRow, 10).Style = editableStyle;
            ws.Cell(excelRow, 10).Value = row.NodeQuantity;
            ws.Cell(excelRow, 11).Style = editableStyle;
            ws.Cell(excelRow, 11).Value = row.AggregateQuantity;
            ws.Cell(excelRow, 12).Style = editableStyle;
            ws.Cell(excelRow, 12).Value = row.ProductQuantity;

            ws.Cell(excelRow, 13).FormulaA1 = $"=I{excelRow}*C{excelRow}*J{excelRow}*K{excelRow}*L{excelRow}";
            ws.Cell(excelRow, 13).Style = gramStyle;
            ws.Cell(excelRow, 14).FormulaA1 = $"=$D${totalCoefficientRow}";
            ws.Cell(excelRow, 14).Style = coefficientStyle;
            ws.Cell(excelRow, 15).FormulaA1 = $"=ROUNDUP(M{excelRow}*N{excelRow},0)";
            ws.Cell(excelRow, 15).Style = gramStyle;

            ws.Cell(excelRow, 16).Style = textStyle;
            ws.Cell(excelRow, 16).Value = row.UnitOfMeasure;
            ws.Cell(excelRow, 17).Style = textStyle;
            ws.Cell(excelRow, 17).Value = row.Periodicity ?? string.Empty;
            ws.Cell(excelRow, 18).Style = textStyle;
            ws.Cell(excelRow, 18).Value = row.Notes ?? string.Empty;
            r++;
        }
        var lastDataRow = r - 1;

        if (dto.Rows.Count == 0)
        {
            ws.Cell(r, 1).Value = "Расчётные строки отсутствуют.";
            r++;
            lastDataRow = r - 1;
        }
        r++;

        // ── Основные марки ГСМ ────────────────────────────────────────────
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "Основные марки ГСМ";
        r++;
        if (dto.PrimaryMaterials.Count > 0)
        {
            string[] primaryHeaders = ["Марка ГСМ", "ГОСТ/ТУ", "Ед. изм.", "Количество строк", "Значение по марке"];
            for (var i = 0; i < primaryHeaders.Length; i++)
            {
                ws.Cell(r, i + 1).Style = headerStyle;
                ws.Cell(r, i + 1).Value = primaryHeaders[i];
            }
            r++;
            foreach (var primary in dto.PrimaryMaterials)
            {
                ws.Cell(r, 1).Style = textStyle;
                ws.Cell(r, 1).Value = primary.MaterialName;
                ws.Cell(r, 2).Style = textStyle;
                ws.Cell(r, 2).Value = primary.Gost ?? string.Empty;
                ws.Cell(r, 3).Style = textStyle;
                ws.Cell(r, 3).Value = primary.UnitOfMeasure;
                ws.Cell(r, 4).Style = textStyle;
                ws.Cell(r, 4).Value = primary.RowCount;
                ws.Cell(r, 5).Style = gramStyle;
                ws.Cell(r, 5).Value = primary.Value;
                r++;
            }
        }
        else
        {
            ws.Cell(r, 1).Value = "Основные марки ГСМ не зафиксированы.";
            r++;
        }
        r++;

        // ── История версий ────────────────────────────────────────────────
        ws.Cell(r, 1).Style = sectionStyle;
        ws.Cell(r, 1).Value = "История версий";
        r++;
        string[] historyHeaders = ["Версия", "Статус", "Создана", "Сформирована", "Архивирована"];
        for (var i = 0; i < historyHeaders.Length; i++)
        {
            ws.Cell(r, i + 1).Style = headerStyle;
            ws.Cell(r, i + 1).Value = historyHeaders[i];
        }
        r++;
        foreach (var entry in dto.History)
        {
            ws.Cell(r, 1).Style = textStyle;
            ws.Cell(r, 1).Value = entry.Version;
            ws.Cell(r, 2).Style = textStyle;
            ws.Cell(r, 2).Value = IndividualCardDisplay.Status(entry.Status);
            ws.Cell(r, 3).Style = textStyle;
            ws.Cell(r, 3).Value = D(entry.CreatedAt);
            ws.Cell(r, 4).Style = textStyle;
            ws.Cell(r, 4).Value = D(entry.FormedAt);
            ws.Cell(r, 5).Style = textStyle;
            ws.Cell(r, 5).Value = D(entry.ArchivedAt);
            r++;
        }
        r += 2;

        ws.Cell(r, 1).Style = textStyle;
        ws.Cell(r, 1).Value = $"Разработал: ____________________ / {dto.CreatedByName ?? string.Empty} / __________";
        r++;
        ws.Cell(r, 1).Style = textStyle;
        ws.Cell(r, 1).Value = "Сформировал: __________________ / ____________________ / __________";

        // ── Формат листа ──────────────────────────────────────────────────
        double[] widths = [6, 26, 9, 16, 15, 15, 15, 14, 10, 9, 9, 9, 12, 12, 12, 7, 12, 22];
        for (var i = 0; i < widths.Length; i++)
            ws.Column(i + 1).Width = widths[i];

        ws.Range(headerRow, 1, Math.Max(lastDataRow, headerRow), LastColumn).SetAutoFilter();
        ws.SheetView.FreezeRows(headerRow);
        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetTop(0.4).SetBottom(0.4).SetLeft(0.4).SetRight(0.4);
        ws.PageSetup.CenterHorizontally = true;

        ws.Protection.Protect(
            XLSheetProtectionElements.SelectEverything |
            XLSheetProtectionElements.AutoFilter |
            XLSheetProtectionElements.Sort |
            XLSheetProtectionElements.FormatColumns |
            XLSheetProtectionElements.FormatRows);
    }

    private static string MaterialText(IReadOnlyList<IndividualCardExportMaterialDto> materials)
    {
        if (materials.Count == 0)
            return string.Empty;
        var sb = new StringBuilder();
        foreach (var material in materials)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(material.MaterialName);
            if (!string.IsNullOrEmpty(material.Gost))
                sb.Append("\nГОСТ/ТУ: ").Append(material.Gost);
        }
        return sb.ToString();
    }

    private static byte[] Save(XLWorkbook workbook)
    {
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static string D(DateTime? value) => value?.ToString("dd.MM.yyyy") ?? string.Empty;
}
