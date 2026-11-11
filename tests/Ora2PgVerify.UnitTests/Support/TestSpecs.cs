using Ora2PgVerify.Model;

namespace Ora2PgVerify.UnitTests.Support;

/// <summary>Catalog definitions exactly as the two dictionaries report the synthetic schema.</summary>
internal static class TestSpecs
{
    public static CatalogTable OracleOrders { get; } = new("APP_OWNER", "ORDERS",
    [
        new("ORDER_ID", "NUMBER", null, 22, 12, 0, false),
        new("CUSTOMER_CODE", "CHAR", 10, 40, null, null, false),
        new("STATUS", "VARCHAR2", 20, 80, null, null, false),
        new("NOTE", "VARCHAR2", 200, 800, null, null, true),
        new("AMOUNT", "NUMBER", null, 22, 14, 2, false),
        new("DISCOUNT_RATE", "NUMBER", null, 22, 5, 4, false),
        new("CREATED_ON", "DATE", null, 7, null, null, false),
        new("SHIPPED_AT", "TIMESTAMP(6)", null, 11, 6, null, true),
        new("UPDATED_AT", "TIMESTAMP(6) WITH TIME ZONE", null, 13, 6, null, false),
    ], ["ORDER_ID"]);

    public static CatalogTable PostgresOrders { get; } = new("app", "orders",
    [
        new("order_id", "bigint", null, null, null, null, false),
        new("customer_code", "character varying", 10, null, null, null, false),
        new("status", "character varying", 20, null, null, null, false),
        new("note", "character varying", 200, null, null, null, true),
        new("amount", "numeric", null, null, 14, 2, false),
        new("discount_rate", "numeric", null, null, 5, 4, false),
        new("created_on", "timestamp without time zone", null, null, 0, null, false),
        new("shipped_at", "timestamp without time zone", null, null, 6, null, true),
        new("updated_at", "timestamp with time zone", null, null, 6, null, false),
    ], ["order_id"]);

    public static CatalogTable OracleDocuments { get; } = new("APP_OWNER", "ORDER_DOCUMENTS",
    [
        new("ORDER_ID", "NUMBER", null, 22, 12, 0, false),
        new("DOC_NO", "NUMBER", null, 22, 3, 0, false),
        new("BODY", "CLOB", null, 4000, null, null, false),
        new("CREATED_AT", "TIMESTAMP(6)", null, 11, 6, null, false),
    ], ["ORDER_ID", "DOC_NO"]);

    public static CatalogTable PostgresDocuments { get; } = new("app", "order_documents",
    [
        new("order_id", "bigint", null, null, null, null, false),
        new("doc_no", "smallint", null, null, null, null, false),
        new("body", "text", null, null, null, null, false),
        new("created_at", "timestamp without time zone", null, null, 6, null, false),
    ], ["order_id", "doc_no"]);

    public static CatalogTable OracleCustomers { get; } = new("APP_OWNER", "CUSTOMERS",
    [
        new("CUSTOMER_CODE", "VARCHAR2", 12, 48, null, null, false),
        new("FULL_NAME", "VARCHAR2", 100, 400, null, null, false),
        new("BIRTH_DATE", "DATE", null, 7, null, null, false),
        new("SEGMENT", "CHAR", 1, 4, null, null, false),
    ], ["CUSTOMER_CODE"]);

    public static CatalogTable PostgresCustomers { get; } = new("app", "customers",
    [
        new("customer_code", "character varying", 12, null, null, null, false),
        new("full_name", "character varying", 100, null, null, null, false),
        new("birth_date", "date", null, null, null, null, false),
        new("segment", "character", 1, null, null, null, false),
    ], ["customer_code"]);

    public static TableSpec Orders { get; } = TableSpecBuilder.Build("orders", OracleOrders, PostgresOrders).Spec!;

    public static TableSpec Documents { get; } = TableSpecBuilder.Build("order_documents", OracleDocuments, PostgresDocuments).Spec!;

    public static TableSpec Customers { get; } = TableSpecBuilder.Build("customers", OracleCustomers, PostgresCustomers).Spec!;
}
