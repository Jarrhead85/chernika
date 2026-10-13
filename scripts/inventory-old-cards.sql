-- Инвентаризация карт прежнего сценария (IsAutonomous = false).
-- Только чтение. Признак — исключительно IsAutonomous, не дата и не название.

SELECT c."Id", c."Code", c."Version", c."Status", c."ObjectLevel", c."BranchId",
       (SELECT count(*) FROM "HKCardItems" i WHERE i."HKCardId" = c."Id") AS item_rows,
       (SELECT count(*) FROM "HKCardItemMaterials" m
          JOIN "HKCardItems" i ON i."Id" = m."HKCardItemId"
         WHERE i."HKCardId" = c."Id") AS material_rows,
       (SELECT count(*) FROM "HKCardNodes" n WHERE n."HKCardId" = c."Id") AS tree_nodes,
       (SELECT count(*) FROM "HKCardComponents" k WHERE k."ParentHKCardId" = c."Id") AS as_parent,
       (SELECT count(*) FROM "HKCardComponents" k WHERE k."ChildHKCardId" = c."Id") AS as_child,
       (SELECT count(*) FROM "HKCardAttachments" a WHERE a."HKCardId" = c."Id") AS attachments,
       (SELECT count(*) FROM "WorkTasks" t WHERE t."EntityId" = c."Id") AS tasks,
       (SELECT count(*) FROM "AuditLogs" al WHERE al."EntityId" = c."Id"::text) AS audit_rows,
       (SELECT count(*) FROM "HKCardStatusLogs" s WHERE s."HKCardId" = c."Id") AS status_log
FROM "HKCards" c
WHERE c."IsAutonomous" = false
ORDER BY c."Code", c."Version";

-- Связи старых карт с сохраняемыми новыми: их удаление затронуло бы новые данные.
SELECT k."ParentHKCardId" AS old_parent, k."ChildHKCardId" AS new_child
FROM "HKCardComponents" k
JOIN "HKCards" p ON p."Id" = k."ParentHKCardId"
JOIN "HKCards" ch ON ch."Id" = k."ChildHKCardId"
WHERE p."IsAutonomous" = false AND ch."IsAutonomous" = true;

SELECT k."ChildHKCardId" AS old_child, k."ParentHKCardId" AS new_parent
FROM "HKCardComponents" k
JOIN "HKCards" p ON p."Id" = k."ParentHKCardId"
JOIN "HKCards" ch ON ch."Id" = k."ChildHKCardId"
WHERE ch."IsAutonomous" = false AND p."IsAutonomous" = true;