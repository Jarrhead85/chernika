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

    // Палитра (print-friendly)
    private const string GridColor = "#B7C3D0";
    private const string SectionFill = "#DCE6F1";
    private const string TableHeaderFill = "#EAF0F7";
    private const string RequisiteLabelFill = "#F3F6FA";
    private const string EditableFill = "#FFF2CC";
    private const string WarningFill = "#FFF4CC";
    private const string WarningTextColor = "#9C6500";
    private const string TextColor = "#1F2937";
    private const string MutedColor = "#5B6573";

    // Служебная скрытая строка с ячейками-донорами стилей (вне области печати)
    private const int StyleRow = 300;

    private sealed class DocStyles
    {
        public IXLStyle Title = null!;
        public IXLStyle Subtitle = null!;
        public IXLStyle Section = null!;
        public IXLStyle TableHeader = null!;
        public IXLStyle RequisiteLabel = null!;
        public IXLStyle RequisiteValue = null!;
        public IXLStyle TextCell = null!;
        public IXLStyle MutedText = null!;
        public IXLStyle VolumeCell = null!;
        public IXLStyle CenterNumberCell = null!;
        public IXLStyle TotalCoefficient = null!;
        public IXLStyle EditableNumber = null!;
        public IXLStyle EditableCoefficient = null!;
        public IXLStyle Warning = null!;
        public IXLStyle Legend = null!;
        public IXLStyle Signature = null!;
    }

    public static IndividualCardXlsxFile Compose(IndividualCardExportDto dto)
    {
        var fileName = ReportFileNameBuilder.Build(dto.Code, dto.Version, ".xlsx");

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("ИК");

        ws.Row(StyleRow).Hide();

        double[] widths =
        [
            5,   // A  № / часть label
            28,  // B  Сборочная единица / часть value
            9,   // C  Кол-во
            21,  // D  Основные марки ГСМ
            20,  // E  Дублирующие марки ГСМ
            20,  // F  Резервные марки ГСМ
            20,  // G  Зарубежные марки ГСМ
            18,  // H  Источник ХК
            12,  // I  Норма ХК
            11,  // J  Кол-во узлов
            13,  // K  Кол-во агрегатов
            12,  // L  Кол-во изделий
            14,  // M  Базовая норма
            12,  // N  Коэффициент
            15,  // O  Расчётная норма
            10,  // P  Ед. изм.
            15,  // Q  Периодичность
            28,  // R  Примечание
        ];
        for (var i = 0; i < widths.Length; i++)
            ws.Column(i + 1).Width = widths[i];

        ws.SheetView.ZoomScale = 85;

        var st = BuildStyles(wb, ws);

        int r = 1;

        // ── Шапка: организация и филиал ───────────────────────────────────
        ws.Range(r, 1, r, 3).Merge();
        ws.Range(r, 1, r, 3).Style = st.RequisiteLabel;
        ws.Cell(r, 1).Value = "Организация:";
        ws.Range(r, 4, r, LastColumn).Merge();
        ws.Range(r, 4, r, LastColumn).Style = st.RequisiteValue;
        ws.Cell(r, 4).Value = "_________________________________________________";
        ws.Row(r).Height = 20.0;
        r++;

        ws.Range(r, 1, r, 3).Merge();
        ws.Range(r, 1, r, 3).Style = st.RequisiteLabel;
        ws.Cell(r, 1).Value = "Филиал:";
        ws.Range(r, 4, r, LastColumn).Merge();
        ws.Range(r, 4, r, LastColumn).Style = st.RequisiteValue;
        ws.Cell(r, 4).Value = dto.BranchName;
        ws.Row(r).Height = 20.0;
        r++;
        ApplyGridBorders(ws, 1, 1, r - 1, LastColumn);
        r++;

        // ── Заголовок документа ───────────────────────────────────────────
        ws.Range(r, 1, r, LastColumn).Merge();
        ws.Range(r, 1, r, LastColumn).Style = st.Title;
        ws.Cell(r, 1).Value = "ИНДИВИДУАЛЬНАЯ КАРТА";
        ws.Row(r).Height = 24.0;
        r++;
        ws.Range(r, 1, r, LastColumn).Merge();
        ws.Range(r, 1, r, LastColumn).Style = st.Subtitle;
        ws.Cell(r, 1).Value = "норм расхода горюче-смазочных материалов";
        ws.Row(r).Height = 20.0;
        r += 2;

        // ── Таблица реквизитов ────────────────────────────────────────────
        var reqFirstRow = r;

        void Pair(string leftLabel, string leftValue, string rightLabel, string rightValue)
        {
            ws.Range(r, 1, r, 3).Merge();
            ws.Range(r, 1, r, 3).Style = st.RequisiteLabel;
            ws.Cell(r, 1).Value = leftLabel;
            ws.Range(r, 4, r, 9).Merge();
            ws.Range(r, 4, r, 9).Style = st.RequisiteValue;
            ws.Cell(r, 4).Value = leftValue;
            ws.Range(r, 10, r, 12).Merge();
            ws.Range(r, 10, r, 12).Style = st.RequisiteLabel;
            ws.Cell(r, 10).Value = rightLabel;
            ws.Range(r, 13, r, LastColumn).Merge();
            ws.Range(r, 13, r, LastColumn).Style = st.RequisiteValue;
            ws.Cell(r, 13).Value = rightValue;
            ws.Row(r).Height = 20.0;
            r++;
        }

        void Full(string label, string value, double height = 30.0)
        {
            ws.Range(r, 1, r, 3).Merge();
            ws.Range(r, 1, r, 3).Style = st.RequisiteLabel;
            ws.Cell(r, 1).Value = label;
            ws.Range(r, 4, r, LastColumn).Merge();
            ws.Range(r, 4, r, LastColumn).Style = st.RequisiteValue;
            ws.Cell(r, 4).Value = value;
            ws.Row(r).Height = height;
            r++;
        }

        Pair("Номер ИК:", dto.Code, "Версия:", dto.Version);
        Pair("Статус:", dto.StatusDisplay, "Уровень объекта:", dto.ObjectLevelDisplay);
        Pair("Создана:", D(dto.CreatedAt), "Автор:", dto.CreatedByName ?? dto.CreatedByUserId);
        Pair("Сформирована:", D(dto.FormedAt), "Архивирована:", D(dto.ArchivedAt));
        Full("Объект:", $"{dto.TargetObjectCode} — {dto.TargetObjectName}");
        if (!string.IsNullOrEmpty(dto.TargetContext))
            Full("Контекст:", dto.TargetContext);
        ApplyGridBorders(ws, reqFirstRow, 1, r - 1, LastColumn);
        r++;

        // ── Особые отметки ────────────────────────────────────────────────
        if (dto.Warnings.Count > 0)
        {
            SectionBand(ws, st, r, "ОСОБЫЕ ОТМЕТКИ");
            r++;
            foreach (var warning in dto.Warnings.OrderBy(w => w.SortOrder))
            {
                ws.Range(r, 1, r, LastColumn).Merge();
                ws.Range(r, 1, r, LastColumn).Style = warning.Code is "DraftNotice" or "HistoricalNotice"
                    ? st.Warning
                    : st.Legend;
                ws.Cell(r, 1).Value = "• " + warning.Message;
                ApplyGridBorders(ws, r, 1, r, LastColumn);
                ws.Row(r).Height = 22.0;
                r++;
            }
            r += 1;
        }

        // ── Версия конструктивного состава ────────────────────────────────
        SectionBand(ws, st, r, "Версия конструктивного состава");
        r++;
        if (dto.Compositions.Count == 0)
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.MutedText;
            ws.Cell(r, 1).Value = "Состав не зафиксирован.";
            ws.Row(r).Height = 18.0;
            r++;
        }
        foreach (var composition in dto.Compositions)
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.TextCell;
            ws.Cell(r, 1).Value = $"{composition.TargetObjectCode} — {composition.TargetObjectName}";
            ws.Row(r).Height = 18.0;
            r++;
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.TextCell;
            ws.Cell(r, 1).Value =
                $"Версия состава: {composition.SourceCompositionVersion}   Количество: {composition.TargetQuantity}   Дата утверждения: {D(composition.SourceApprovedAt)}";
            ws.Row(r).Height = 18.0;
            r++;
            foreach (var aggregate in composition.Aggregates)
            {
                ws.Range(r, 1, r, LastColumn).Merge();
                ws.Range(r, 1, r, LastColumn).Style = st.TextCell;
                ws.Cell(r, 1).Value = $"    Агрегат: {aggregate.Code} — {aggregate.Name} ×{aggregate.Quantity}";
                ws.Row(r).Height = 18.0;
                r++;
                foreach (var node in aggregate.Nodes)
                {
                    ws.Range(r, 1, r, LastColumn).Merge();
                    ws.Range(r, 1, r, LastColumn).Style = st.TextCell;
                    ws.Cell(r, 1).Value = $"        Узел: {node.Code} — {node.Name} ×{node.Quantity}";
                    ws.Row(r).Height = 18.0;
                    r++;
                }
            }
        }
        r++;

        // ── Нормативные источники ХК ──────────────────────────────────────
        SectionBand(ws, st, r, "Нормативные источники ХК");
        r++;
        if (dto.HKSources.Count > 0)
        {
            string[] sourceHeaders =
            [
                "Уровень", "Объект", "ХК", "Версия", "Утверждена", "Начало действия",
                "Окончание действия", "Готовность",
            ];
            for (var i = 0; i < sourceHeaders.Length; i++)
            {
                ws.Cell(r, i + 1).Style = st.TableHeader;
                ws.Cell(r, i + 1).Value = sourceHeaders[i];
            }
            ws.Row(r).Height = 26.0;
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

                ws.Cell(r, 1).Style = st.TextCell;
                ws.Cell(r, 1).Value = source.ObjectLevelDisplay;
                ws.Cell(r, 2).Style = st.TextCell;
                ws.Cell(r, 2).Value = new string(' ', depth * 3) + $"{source.SourceObjectCode} — {source.SourceObjectName}";
                ws.Cell(r, 3).Style = st.TextCell;
                ws.Cell(r, 3).Value = source.HKCardCode;
                ws.Cell(r, 4).Style = st.TextCell;
                ws.Cell(r, 4).Value = source.HKCardVersion;
                ws.Cell(r, 5).Style = st.TextCell;
                ws.Cell(r, 5).Value = D(source.ApprovedAt);
                ws.Cell(r, 6).Style = st.TextCell;
                ws.Cell(r, 6).Value = D(source.EffectiveDate);
                ws.Cell(r, 7).Style = st.TextCell;
                ws.Cell(r, 7).Value = D(source.ExpirationDate);
                ws.Cell(r, 8).Style = st.TextCell;
                ws.Cell(r, 8).Value = source.IsComplete ? "Полная" : "Частичная";
                ApplyGridBorders(ws, r, 1, r, 8);
                ws.Row(r).Height = 20.0;
                r++;
            }
        }
        else
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.MutedText;
            ws.Cell(r, 1).Value = "Источники ХК не зафиксированы.";
            ws.Row(r).Height = 18.0;
            r++;
        }
        r++;

        // ── Применённые коэффициенты ──────────────────────────────────────
        SectionBand(ws, st, r, "Применённые коэффициенты");
        r++;

        ws.Range(r, 1, r, LastColumn).Merge();
        ws.Range(r, 1, r, LastColumn).Style = st.Legend;
        ws.Cell(r, 1).Value = "Ячейки с выделенной заливкой допускают локальное изменение для пересчёта в Excel. " +
                              "Изменения не вносятся в ИК в системе.";
        ApplyGridBorders(ws, r, 1, r, LastColumn);
        ws.Row(r).Height = 30.0;
        r += 2;

        var coefficientFirstDataRow = r + 1;
        if (dto.Coefficients.Count > 0)
        {
            // Шапка: A:C Тип | D:F Наименование | G:H Значение | I:L Условия | M:R Основание
            int[][] coefficientHead = [[1, 3], [4, 6], [7, 8], [9, 12], [13, 18]];
            string[] coefficientHeaders = ["Тип", "Наименование", "Значение", "Условия применения", "Нормативное основание"];
            for (var i = 0; i < coefficientHead.Length; i++)
            {
                var c1 = coefficientHead[i][0];
                var c2 = coefficientHead[i][1];
                ws.Range(r, c1, r, c2).Merge();
                ws.Range(r, c1, r, c2).Style = st.TableHeader;
                ws.Cell(r, c1).Value = coefficientHeaders[i];
            }
            ApplyGridBorders(ws, r, 1, r, LastColumn);
            ws.Row(r).Height = 24.0;
            r++;

            foreach (var coefficient in dto.Coefficients)
            {
                ws.Range(r, 1, r, 3).Merge();
                ws.Range(r, 1, r, 3).Style = st.TextCell;
                ws.Cell(r, 1).Value = coefficient.TypeName;
                ws.Range(r, 4, r, 6).Merge();
                ws.Range(r, 4, r, 6).Style = st.TextCell;
                ws.Cell(r, 4).Value = coefficient.Name;
                ws.Range(r, 7, r, 8).Merge();
                ws.Range(r, 7, r, 8).Style = st.EditableCoefficient;
                ws.Cell(r, 7).Value = coefficient.Value;
                ws.Range(r, 9, r, 12).Merge();
                ws.Range(r, 9, r, 12).Style = st.TextCell;
                ws.Cell(r, 9).Value = coefficient.ConditionDescription ?? string.Empty;
                ws.Range(r, 13, r, 18).Merge();
                ws.Range(r, 13, r, 18).Style = st.TextCell;
                ws.Cell(r, 13).Value = coefficient.NormativeBasis ?? string.Empty;
                ws.Row(r).Height = 22.0;
                r++;
            }
            var coefficientLastDataRow = r - 1;
            ApplyGridBorders(ws, coefficientFirstDataRow - 1, 1, coefficientLastDataRow, LastColumn);

            // Итоговый коэффициент
            ws.Range(r, 1, r, 6).Merge();
            ws.Range(r, 1, r, 6).Style = st.RequisiteLabel;
            ws.Cell(r, 1).Value = "Общий коэффициент:";
            ws.Range(r, 7, r, 8).Merge();
            ws.Range(r, 7, r, 8).Style = st.TotalCoefficient;
            ws.Cell(r, 7).FormulaA1 = $"=PRODUCT(G{coefficientFirstDataRow}:G{coefficientLastDataRow})";
            ApplyGridBorders(ws, r, 1, r, LastColumn);
            ws.Row(r).Height = 22.0;
            var totalCoefficientRow = r;
            r += 2;
            var bodyScopes = ComposeNorms(wb, ws, dto, st, totalCoefficientRow, r);
            ApplySheetSetup(ws, bodyScopes.HeaderRow, bodyScopes.LastDataRow, bodyScopes.LastContentRow);
        }
        else
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.MutedText;
            ws.Cell(r, 1).Value = "Коэффициенты не применялись.";
            ws.Row(r).Height = 18.0;
            r += 2;

            ws.Range(r, 1, r, 6).Merge();
            ws.Range(r, 1, r, 6).Style = st.RequisiteLabel;
            ws.Cell(r, 1).Value = "Общий коэффициент:";
            ws.Range(r, 7, r, 8).Merge();
            ws.Range(r, 7, r, 8).Style = st.TotalCoefficient;
            ws.Cell(r, 7).Value = 1m;
            ws.Row(r).Height = 22.0;
            var totalCoefficientRow = r;
            r += 2;
            var bodyScopes = ComposeNorms(wb, ws, dto, st, totalCoefficientRow, r);
            ApplySheetSetup(ws, bodyScopes.HeaderRow, bodyScopes.LastDataRow, bodyScopes.LastContentRow);
        }

        return new IndividualCardXlsxFile(fileName, Save(wb));
    }

    private static void SectionBand(IXLWorksheet ws, DocStyles st, int row, string title)
    {
        ws.Range(row, 1, row, LastColumn).Merge();
        ws.Range(row, 1, row, LastColumn).Style = st.Section;
        ws.Cell(row, 1).Value = title;
        ApplyGridBorders(ws, row, 1, row, LastColumn);
        ws.Row(row).Height = 21.0;
    }

    private static (int LastContentRow, int HeaderRow, int LastDataRow) ComposeNorms(
        XLWorkbook wb, IXLWorksheet ws, IndividualCardExportDto dto, DocStyles st,
        int totalCoefficientRow, int r)
    {
        SectionBand(ws, st, r, "Нормы расхода ГСМ");
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
            ws.Cell(r, i + 1).Style = st.TableHeader;
            ws.Cell(r, i + 1).Value = headers[i];
        }
        ws.Row(r).Height = 42.0;
        r++;

        var firstDataRow = r;
        foreach (var row in dto.Rows.OrderBy(x => x.SortOrder))
        {
            var excelRow = r;
            Center(ws, excelRow, 1, st.CenterNumberCell, row.SortOrder);
            ws.Cell(excelRow, 2).Style = st.TextCell;
            ws.Cell(excelRow, 2).Value = $"{row.AssemblyUnitCode} — {row.AssemblyUnitName}";

            Editable(ws, excelRow, 3, st.EditableNumber, row.AssemblyUnitQuantity);
            ws.Cell(excelRow, 4).Style = st.TextCell;
            ws.Cell(excelRow, 4).Value = MaterialText(row.PrimaryMaterials);
            ws.Cell(excelRow, 5).Style = st.TextCell;
            ws.Cell(excelRow, 5).Value = MaterialText(row.DuplicateMaterials);
            ws.Cell(excelRow, 6).Style = st.TextCell;
            ws.Cell(excelRow, 6).Value = MaterialText(row.ReserveMaterials);
            ws.Cell(excelRow, 7).Style = st.TextCell;
            ws.Cell(excelRow, 7).Value = MaterialText(row.ForeignMaterials);
            ws.Cell(excelRow, 8).Style = st.TextCell;
            ws.Cell(excelRow, 8).Value = $"{row.SourceHKCardCode} {row.SourceHKCardVersion}";
            Right(ws, excelRow, 9, st.VolumeCell, row.SourceVolume);
            Editable(ws, excelRow, 10, st.EditableNumber, row.NodeQuantity);
            Editable(ws, excelRow, 11, st.EditableNumber, row.AggregateQuantity);
            Editable(ws, excelRow, 12, st.EditableNumber, row.ProductQuantity);

            var baseCell = ws.Cell(excelRow, 13);
            baseCell.FormulaA1 = $"=I{excelRow}*C{excelRow}*J{excelRow}*K{excelRow}*L{excelRow}";
            baseCell.Style = st.VolumeCell;
            var coeffCell = ws.Cell(excelRow, 14);
            coeffCell.FormulaA1 = $"=$G${totalCoefficientRow}";
            coeffCell.Style = st.TotalCoefficient;
            var calcCell = ws.Cell(excelRow, 15);
            calcCell.FormulaA1 = $"=ROUNDUP(M{excelRow}*N{excelRow},0)";
            calcCell.Style = st.VolumeCell;

            Center(ws, excelRow, 16, st.TextCell, row.UnitOfMeasure);
            Center(ws, excelRow, 17, st.TextCell, row.Periodicity ?? string.Empty);
            ws.Cell(excelRow, 18).Style = st.TextCell;
            ws.Cell(excelRow, 18).Value = row.Notes ?? string.Empty;

            var materialLines =
                row.PrimaryMaterials.Count + row.DuplicateMaterials.Count + row.ReserveMaterials.Count +
                row.ForeignMaterials.Count;
            ws.Row(excelRow).Height = Math.Max(32.0, 18.0 * (materialLines + 1));
            r++;
        }
        var lastDataRow = r - 1;

        if (dto.Rows.Count == 0)
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.MutedText;
            ws.Cell(r, 1).Value = "Расчётные строки отсутствуют.";
            lastDataRow = r;
            r++;
        }
        r++;

        // ── Основные марки ГСМ ────────────────────────────────────────────
        SectionBand(ws, st, r, "Основные марки ГСМ");
        r++;
        if (dto.PrimaryMaterials.Count > 0)
        {
            string[] primaryHeaders = ["Марка ГСМ", "ГОСТ/ТУ", "Ед. изм.", "Количество строк", "Значение по марке"];
            for (var i = 0; i < primaryHeaders.Length; i++)
            {
                ws.Cell(r, i + 1).Style = st.TableHeader;
                ws.Cell(r, i + 1).Value = primaryHeaders[i];
            }
            ws.Row(r).Height = 24.0;
            r++;
            foreach (var primary in dto.PrimaryMaterials)
            {
                ws.Cell(r, 1).Style = st.TextCell;
                ws.Cell(r, 1).Value = primary.MaterialName;
                ws.Cell(r, 2).Style = st.TextCell;
                ws.Cell(r, 2).Value = primary.Gost ?? string.Empty;
                Center(ws, r, 3, st.TextCell, primary.UnitOfMeasure);
                Center(ws, r, 4, st.CenterNumberCell, primary.RowCount);
                Right(ws, r, 5, st.VolumeCell, primary.Value);
                ApplyGridBorders(ws, r, 1, r, 5);
                ws.Row(r).Height = 20.0;
                r++;
            }
        }
        else
        {
            ws.Range(r, 1, r, LastColumn).Merge();
            ws.Range(r, 1, r, LastColumn).Style = st.MutedText;
            ws.Cell(r, 1).Value = "Основные марки ГСМ не зафиксированы.";
            ws.Row(r).Height = 18.0;
            r++;
        }
        r++;

        // ── История версий ────────────────────────────────────────────────
        SectionBand(ws, st, r, "История версий");
        r++;
        string[] historyHeaders = ["Версия", "Статус", "Создана", "Сформирована", "Архивирована"];
        for (var i = 0; i < historyHeaders.Length; i++)
        {
            ws.Cell(r, i + 1).Style = st.TableHeader;
            ws.Cell(r, i + 1).Value = historyHeaders[i];
        }
        ws.Row(r).Height = 24.0;
        r++;
        foreach (var entry in dto.History)
        {
            ws.Cell(r, 1).Style = st.TextCell;
            ws.Cell(r, 1).Value = entry.Version;
            ws.Cell(r, 2).Style = st.TextCell;
            ws.Cell(r, 2).Value = IndividualCardDisplay.Status(entry.Status);
            Center(ws, r, 3, st.TextCell, D(entry.CreatedAt));
            Center(ws, r, 4, st.TextCell, D(entry.FormedAt));
            Center(ws, r, 5, st.TextCell, D(entry.ArchivedAt));
            ApplyGridBorders(ws, r, 1, r, 5);
            ws.Row(r).Height = 20.0;
            r++;
        }
        r += 2;

        // ── Подписи ───────────────────────────────────────────────────────
        ws.Range(r, 1, r, 9).Merge();
        ws.Range(r, 1, r, 9).Style = st.Signature;
        ws.Cell(r, 1).Value = "Разработал: ____________________";
        ws.Range(r, 10, r, 14).Merge();
        ws.Range(r, 10, r, 14).Style = st.Signature;
        ws.Cell(r, 10).Value = $"/ {dto.CreatedByName ?? string.Empty} /";
        ws.Range(r, 15, r, LastColumn).Merge();
        ws.Range(r, 15, r, LastColumn).Style = st.Signature;
        ws.Cell(r, 15).Value = "__________";
        ws.Row(r).Height = 22.0;
        r++;
        ws.Range(r, 1, r, 9).Merge();
        ws.Range(r, 1, r, 9).Style = st.Signature;
        ws.Cell(r, 1).Value = "Сформировал: __________________";
        ws.Range(r, 10, r, 14).Merge();
        ws.Range(r, 10, r, 14).Style = st.Signature;
        ws.Cell(r, 10).Value = "/ ____________________ /";
        ws.Range(r, 15, r, LastColumn).Merge();
        ws.Range(r, 15, r, LastColumn).Style = st.Signature;
        ws.Cell(r, 15).Value = "__________";
        ws.Row(r).Height = 22.0;
        var lastContentRow = r;

        ApplyGridBorders(ws, headerRow, 1, Math.Max(lastDataRow, headerRow), LastColumn);

        return (lastContentRow, headerRow, lastDataRow);
    }

    private static void Center(IXLWorksheet ws, int row, int col, IXLStyle style, XLCellValue value)
    {
        var cell = ws.Cell(row, col);
        cell.Style = style;
        cell.Value = value;
    }

    private static void Right(IXLWorksheet ws, int row, int col, IXLStyle style, XLCellValue value)
    {
        var cell = ws.Cell(row, col);
        cell.Style = style;
        cell.Value = value;
    }

    private static void Editable(IXLWorksheet ws, int row, int col, IXLStyle style, XLCellValue value)
    {
        var cell = ws.Cell(row, col);
        cell.Style = style;
        cell.Value = value;
    }

    private static void ApplyGridBorders(IXLWorksheet ws, int row1, int col1, int row2, int col2)
    {
        var rg = ws.Range(row1, col1, row2, col2);
        rg.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        rg.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin);
        rg.Style.Border.SetOutsideBorderColor(XLColor.FromHtml(GridColor));
        rg.Style.Border.SetInsideBorderColor(XLColor.FromHtml(GridColor));
    }

    private static void ApplySheetSetup(IXLWorksheet ws, int headerRow, int lastDataRow, int lastContentRow)
    {
        ws.Range(headerRow, 1, Math.Max(lastDataRow, headerRow), LastColumn).SetAutoFilter();
        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.SetTop(0.4).SetBottom(0.4).SetLeft(0.4).SetRight(0.4);
        ws.PageSetup.CenterHorizontally = true;

        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add($"A1:R{lastContentRow}");

        ws.Protection.Protect(
            XLSheetProtectionElements.SelectEverything |
            XLSheetProtectionElements.AutoFilter |
            XLSheetProtectionElements.Sort |
            XLSheetProtectionElements.FormatColumns |
            XLSheetProtectionElements.FormatRows);
    }

    private static DocStyles BuildStyles(XLWorkbook wb, IXLWorksheet ws)
    {
        IXLStyle S(int column, Action<IXLStyle> configure)
        {
            var style = ws.Cell(StyleRow, column).Style;
            configure(style);
            return style;
        }

        return new DocStyles
        {
            Title = S(1, s =>
            {
                s.Font.SetFontSize(16.0);
                s.Font.SetBold();
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            }),
            Subtitle = S(2, s =>
            {
                s.Font.SetFontSize(11.0);
                s.Font.SetBold();
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            }),
            Section = S(3, s =>
            {
                s.Font.SetFontSize(11.0);
                s.Font.SetBold();
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(SectionFill));
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left);
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            }),
            TableHeader = S(4, s =>
            {
                s.Font.SetFontSize(9.0);
                s.Font.SetBold();
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(TableHeaderFill));
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetWrapText(true);
            }),
            RequisiteLabel = S(5, s =>
            {
                s.Font.SetBold();
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(RequisiteLabelFill));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetWrapText(true);
            }),
            RequisiteValue = S(6, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetWrapText(true);
            }),
            TextCell = S(7, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
                s.Alignment.SetWrapText(true);
            }),
            MutedText = S(8, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(MutedColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
                s.Alignment.SetWrapText(true);
            }),
            VolumeCell = S(9, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
                s.NumberFormat.SetFormat(GramFormat);
            }),
            CenterNumberCell = S(10, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }),
            TotalCoefficient = S(11, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                s.NumberFormat.SetFormat(CoefficientFormat);
            }),
            EditableNumber = S(12, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(EditableFill));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                s.Alignment.SetWrapText(true);
                s.NumberFormat.SetFormat(GramFormat);
                s.Protection.SetLocked(false);
            }),
            EditableCoefficient = S(13, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(EditableFill));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
                s.NumberFormat.SetFormat(CoefficientFormat);
                s.Protection.SetLocked(false);
            }),
            Warning = S(14, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(WarningTextColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(WarningFill));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetWrapText(true);
            }),
            Legend = S(15, s =>
            {
                s.Font.SetItalic();
                s.Font.SetFontColor(XLColor.FromHtml(MutedColor));
                s.Fill.SetBackgroundColor(XLColor.FromHtml(WarningFill));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
                s.Alignment.SetWrapText(true);
            }),
            Signature = S(16, s =>
            {
                s.Font.SetFontColor(XLColor.FromHtml(TextColor));
                s.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            }),
        };
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
