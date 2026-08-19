using Chernika.Domain.Entities;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Chernika.Infrastructure.Services;

public class ReportService
{
    public byte[] GenerateHKCardPdf(HKCard card)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(TextStyle.Default.FontSize(10).FontFamily("Arial"));

                page.Header()
                    .Column(col =>
                    {
                        col.Item().Text("ХИММОТОЛОГИЧЕСКАЯ КАРТА").FontSize(16).Bold().AlignCenter();
                        col.Item().Text($"Шифр: {card.Code}  |  Версия: {card.Version}  |  Статус: {card.Status}").FontSize(10).AlignCenter();
                        if (card.SupersedesHKCard != null)
                        {
                            col.Item().Text($"Заменяет: {card.SupersedesHKCard.Code}, версия {card.SupersedesHKCard.Version}").FontSize(9).AlignCenter();
                        }
                        col.Item().LineHorizontal(1);
                    });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(1);
                            columns.RelativeColumn(2);
                        });

                        table.Cell().Border(1).Padding(5).Text("Узел:").Bold();
                        table.Cell().Border(1).Padding(5).Text(card.Node?.Name ?? "—");

                        table.Cell().Border(1).Padding(5).Text("Филиал:").Bold();
                        table.Cell().Border(1).Padding(5).Text(card.Branch?.Name ?? "—");

                        table.Cell().Border(1).Padding(5).Text("Дата утверждения:").Bold();
                        table.Cell().Border(1).Padding(5).Text(card.ApprovedDate?.ToString("dd.MM.yyyy") ?? "—");

                        table.Cell().Border(1).Padding(5).Text("Срок действия:").Bold();
                        table.Cell().Border(1).Padding(5).Text($"{card.EffectiveDate?.ToString("dd.MM.yyyy") ?? "—"} — {card.ExpirationDate?.ToString("dd.MM.yyyy") ?? "—"}");
                    });

                    col.Item().PaddingTop(10).Text("Сборочные единицы и нормы ГСМ:").Bold().FontSize(12);

                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(1);
                            columns.RelativeColumn(1);
                            columns.RelativeColumn(1);
                            columns.RelativeColumn(2);
                        });

                        table.Header(header =>
                        {
                            header.Cell().Border(1).Background(Colors.Grey.Lighten2).Padding(5).Text("Сборочная единица").Bold();
                            header.Cell().Border(1).Background(Colors.Grey.Lighten2).Padding(5).Text("Кол-во").Bold();
                            header.Cell().Border(1).Background(Colors.Grey.Lighten2).Padding(5).Text("Объём").Bold();
                            header.Cell().Border(1).Background(Colors.Grey.Lighten2).Padding(5).Text("Ед.изм.").Bold();
                            header.Cell().Border(1).Background(Colors.Grey.Lighten2).Padding(5).Text("Периодичность").Bold();
                        });

                        foreach (var item in card.Items.OrderBy(i => i.SortOrder))
                        {
                            table.Cell().Border(1).Padding(5).Text(item.AssemblyUnit?.Name ?? "—");
                            table.Cell().Border(1).Padding(5).Text(item.Quantity.ToString());
                            table.Cell().Border(1).Padding(5).Text(item.Volume.ToString("F3"));
                            table.Cell().Border(1).Padding(5).Text(item.UnitOfMeasure ?? "кг");
                            table.Cell().Border(1).Padding(5).Text(item.Periodicity ?? "—");
                        }
                    });

                    if (!string.IsNullOrEmpty(card.Purpose))
                    {
                        col.Item().PaddingTop(10).Text("Назначение:").Bold();
                        col.Item().Text(card.Purpose).FontSize(9);
                    }

                    if (!string.IsNullOrEmpty(card.NormativeBasis))
                    {
                        col.Item().PaddingTop(5).Text("Основание для разработки:").Bold();
                        col.Item().Text(card.NormativeBasis).FontSize(9);
                    }

                    if (!string.IsNullOrEmpty(card.Notes))
                    {
                        col.Item().PaddingTop(5).Text("Примечание:").Bold();
                        col.Item().Text(card.Notes).FontSize(9);
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(8);
                    t.Span(" из ").FontSize(8);
                    t.TotalPages().FontSize(8);
                });
            });
        });

        return document.GeneratePdf();
    }

    public byte[] GenerateHKRegistryExcel(List<HKCard> cards)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Реестр ХК");

        WriteHKRegistryHeaders(worksheet);

        int row = 2;
        foreach (var card in cards)
            WriteHKRegistryRow(worksheet, card, ref row);

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<Stream> GenerateHKRegistryExcelAsync(IQueryable<HKCard> query)
    {
        var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Реестр ХК");

        WriteHKRegistryHeaders(worksheet);

        int row = 2;
        await foreach (var card in query.AsAsyncEnumerable())
            WriteHKRegistryRow(worksheet, card, ref row);

        worksheet.Columns().AdjustToContents();

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        workbook.Dispose();
        return stream;
    }

    private static void WriteHKRegistryHeaders(IXLWorksheet worksheet)
    {
        var headers = new[] { "№ ХК", "Версия", "Статус", "Узел", "Филиал", "Дата создания", "Дата утверждения", "Строк" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = worksheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x0F, 0x1B, 0x33);
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void WriteHKRegistryRow(IXLWorksheet worksheet, HKCard card, ref int row)
    {
        worksheet.Cell(row, 1).Value = card.Code;
        worksheet.Cell(row, 2).Value = card.Version;
        worksheet.Cell(row, 3).Value = card.Status.ToString();
        worksheet.Cell(row, 4).Value = card.Node?.Name ?? "—";
        worksheet.Cell(row, 5).Value = card.Branch?.Name ?? "—";
        worksheet.Cell(row, 6).Value = card.CreatedAt.ToString("dd.MM.yyyy");
        worksheet.Cell(row, 7).Value = card.ApprovedDate?.ToString("dd.MM.yyyy") ?? "—";
        worksheet.Cell(row, 8).Value = card.Items.Count;

        var statusCell = worksheet.Cell(row, 3);
        if (card.Status == Domain.Enums.HKCardStatus.Approved)
            statusCell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x26, 0xD0, 0x7C);
        else if (card.Status == Domain.Enums.HKCardStatus.OnReview)
            statusCell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xFF, 0xCC, 0x66);
        else if (card.Status == Domain.Enums.HKCardStatus.RevisionRequired)
            statusCell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xFF, 0x6B, 0x6B);

        row++;
    }
}
