BEGIN;
DELETE FROM tenant_e47808f00dd5.products WHERE "Sku" IN (
'PRD-001','PRD-002','PRD-003','PRD-004','PRD-005','PRD-006','PRD-007','PRD-008','PRD-009','PRD-010','PRD-011','PRD-012','PRD-013','PRD-014','PRD-015','PRD-016','PRD-017','PRD-018','PRD-019','PRD-020','PRD-021','PRD-022','PRD-023','PRD-024','PRD-100'
);
INSERT INTO tenant_e47808f00dd5.products ("Id", "Sku", "Barcode", "Name", "CategoryId", "Unit", "PurchasePrice", "SalePrice", "VatRate", "CurrentStock", "MinStock", "IsActive", "IsService", "ImageUrl", "Description", "IsVisibleOnMenu", "MenuSortOrder", "CreatedAt", "UpdatedAt") VALUES
('80dfc39c-4fd7-4afc-976a-0063a55afae6','PRD-001',NULL,'Espresso','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',5.00,10.00,20.00,50.00,10.00,true,false,NULL,NULL,true,1,now(),NULL),
('8159f020-082f-4dcc-b4d0-46c56c534fb5','PRD-002',NULL,'Americano','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',6.00,12.00,20.00,50.00,10.00,true,false,NULL,NULL,true,2,now(),NULL),
('d569bae2-1063-45da-8eb0-7f5ea1769308','PRD-003',NULL,'Cappuccino','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',7.00,14.00,20.00,40.00,8.00,true,false,NULL,NULL,true,3,now(),NULL),
('92659cb6-01d1-4781-9cc6-881fbac4650b','PRD-004',NULL,'Latte','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',7.50,15.00,20.00,40.00,8.00,true,false,NULL,NULL,true,4,now(),NULL),
('d4c352fb-5f7a-4e2e-971f-b530099b55ff','PRD-005',NULL,'Mocha','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',8.00,16.00,20.00,35.00,8.00,true,false,NULL,NULL,true,5,now(),NULL),
('14318eb4-c575-4fe2-a2b3-2540fd6be53f','PRD-006',NULL,'Turkish Coffee','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',4.00,8.00,20.00,60.00,10.00,true,false,NULL,NULL,true,6,now(),NULL),
('53fb288c-9743-40e8-a9a2-6bf4acf70388','PRD-007',NULL,'Hot Chocolate','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',6.50,13.00,20.00,30.00,5.00,true,false,NULL,NULL,true,7,now(),NULL),
('df1c7780-988c-4510-bf50-0f0a16e1911a','PRD-008',NULL,'Chai Latte','0454b31f-3d31-449c-9327-2d27ec3400b9','cup',7.00,14.00,20.00,30.00,5.00,true,false,NULL,NULL,true,8,now(),NULL),
('ccb8ccac-c90b-45cc-86c1-480e1dbba94d','PRD-009',NULL,'Iced Americano','95697c37-6aac-490d-b352-c034e62aeddd','cup',6.50,13.00,20.00,40.00,8.00,true,false,NULL,NULL,true,1,now(),NULL),
('5c610398-f0b4-4908-a123-9f16659a26bd','PRD-010',NULL,'Iced Latte','95697c37-6aac-490d-b352-c034e62aeddd','cup',8.00,16.00,20.00,35.00,8.00,true,false,NULL,NULL,true,2,now(),NULL),
('7f3628e8-08bd-4391-bf3b-e8bce7ab8c7f','PRD-011',NULL,'Iced Mocha','95697c37-6aac-490d-b352-c034e62aeddd','cup',8.50,17.00,20.00,30.00,8.00,true,false,NULL,NULL,true,3,now(),NULL),
('9a57a8ba-0f69-49d5-b6e0-b03f5ebe78a6','PRD-012',NULL,'Iced Tea','95697c37-6aac-490d-b352-c034e62aeddd','cup',5.00,10.00,20.00,40.00,8.00,true,false,NULL,NULL,true,4,now(),NULL),
('34784e9d-c271-48dc-af1b-42265b3ffe67','PRD-013',NULL,'Smoothie','95697c37-6aac-490d-b352-c034e62aeddd','cup',12.00,24.00,20.00,25.00,5.00,true,false,NULL,NULL,true,5,now(),NULL),
('18f5e268-250f-494b-a4fc-37ccf4b925e4','PRD-014',NULL,'Lemonade','95697c37-6aac-490d-b352-c034e62aeddd','cup',7.00,14.00,20.00,30.00,5.00,true,false,NULL,NULL,true,6,now(),NULL),
('67c80aee-b891-4a24-a94f-27aad5d48d61','PRD-015',NULL,'Cold Coffee','95697c37-6aac-490d-b352-c034e62aeddd','cup',6.50,13.00,20.00,35.00,8.00,true,false,NULL,NULL,true,7,now(),NULL),
('f8271eda-2d26-4dd0-8017-5951c506839e','PRD-016',NULL,'Iced Green Tea','95697c37-6aac-490d-b352-c034e62aeddd','cup',5.50,11.00,20.00,30.00,5.00,true,false,NULL,NULL,true,8,now(),NULL),
('bfe8f444-ed26-4d71-be6c-82127f384120','PRD-017',NULL,'Croissant','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',4.00,9.00,20.00,50.00,10.00,true,false,NULL,NULL,true,1,now(),NULL),
('7851af95-a95a-4616-94ac-4cdaf5eb0b6f','PRD-018',NULL,'Sandwich','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',15.00,30.00,20.00,20.00,5.00,true,false,NULL,NULL,true,2,now(),NULL),
('f3cd9d00-64d1-4207-8a9a-1856125bb237','PRD-019',NULL,'Baguette','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',12.00,24.00,20.00,20.00,5.00,true,false,NULL,NULL,true,3,now(),NULL),
('0d38940e-dae1-496d-82a4-5d9140d6a947','PRD-020',NULL,'Pastry','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',4.50,9.00,20.00,60.00,10.00,true,false,NULL,NULL,true,4,now(),NULL),
('764471b2-a94e-4718-9545-e56376bc3ca1','PRD-021',NULL,'Cookie','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',5.00,10.00,20.00,40.00,10.00,true,false,NULL,NULL,true,5,now(),NULL),
('34514009-4426-4ded-90e1-c0a52a6a4866','PRD-022',NULL,'Cake','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',8.00,16.00,20.00,30.00,5.00,true,false,NULL,NULL,true,6,now(),NULL),
('7d004e10-8b2e-4041-a3c0-245281181e6f','PRD-023',NULL,'Cake Slice','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',18.00,36.00,20.00,15.00,5.00,true,false,NULL,NULL,true,7,now(),NULL),
('1d42250a-27b1-4a86-8b84-bdb61c924198','PRD-024',NULL,'Salad','cf501b68-ae8f-45f0-b591-452d1bbb4527e52','piece',14.00,28.00,20.00,20.00,5.00,true,false,NULL,NULL,true,8,now(),NULL);
COMMIT;
SELECT COUNT(*) AS total_products, SUM(CASE WHEN "IsActive" THEN 1 ELSE 0 END) AS active_products FROM tenant_e47808f00dd5.products;
SELECT "Sku", "Name", "CategoryId", "CurrentStock" FROM tenant_e47808f00dd5.products WHERE "Sku" LIKE 'PRD-%' ORDER BY "Sku";