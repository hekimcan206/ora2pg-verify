CREATE OR REPLACE PROCEDURE LOG_EVENT(p_event IN VARCHAR2, p_count IN NUMBER) IS
  PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
  INSERT INTO tmp_order_ids (order_id) VALUES (ORDER_SEQ.NEXTVAL);
  DBMS_OUTPUT.PUT_LINE(p_event || ': ' || p_count);
  COMMIT;
EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('log failed: ' || SQLCODE);
    ROLLBACK;
END LOG_EVENT;
/
CREATE OR REPLACE PACKAGE BILLING_PKG AS
  -- Package state: one value per database session, for the life of the session.
  g_last_run      DATE;
  c_vat_rate      CONSTANT NUMBER := 0.20;
  e_no_orders     EXCEPTION;
  TYPE t_id_list IS TABLE OF NUMBER;

  PROCEDURE close_month(p_month IN DATE);
  FUNCTION order_total(p_order_id IN NUMBER) RETURN NUMBER;
  g_run_count     PLS_INTEGER := 0;
END BILLING_PKG;
/
CREATE OR REPLACE PACKAGE BODY BILLING_PKG AS
  g_cache_hits    PLS_INTEGER := 0;

  FUNCTION order_total(p_order_id IN NUMBER) RETURN NUMBER IS
    v_total NUMBER;
  BEGIN
    SELECT NVL(o.amount, 0) * (1 - NVL(o.discount_rate, 0)) * (1 + c_vat_rate)
      INTO v_total
      FROM orders o
     WHERE o.order_id = p_order_id;
    RETURN v_total;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RAISE_APPLICATION_ERROR(-20001, 'order ' || p_order_id || ' not found');
  END order_total;

  PROCEDURE close_month(p_month IN DATE) IS
    v_ids   t_id_list;
    v_label VARCHAR2(200);
  BEGIN
    g_last_run := SYSDATE;
    g_run_count := g_run_count + 1;

    SELECT /*+ INDEX(o) */ o.order_id BULK COLLECT INTO v_ids
      FROM orders o, customers c
     WHERE o.customer_code = c.customer_code(+)
       AND o.created_on >= TRUNC(p_month, 'MM')
       AND o.created_on < ADD_MONTHS(TRUNC(p_month, 'MM'), 1)
       AND ROWNUM <= 10000;

    FORALL i IN 1 .. v_ids.COUNT
      UPDATE orders SET status = DECODE(status, 'PAID', 'CLOSED', status) WHERE order_id = v_ids(i);

    SELECT LISTAGG(status, ',') WITHIN GROUP (ORDER BY status) INTO v_label
      FROM (SELECT DISTINCT status FROM orders MINUS SELECT 'CANCELLED' FROM dual);

    IF v_label = '' THEN
      v_label := NULL; -- never reached in Oracle: '' is NULL, so the comparison is never true
    END IF;

    MERGE INTO accounts a
    USING (SELECT 1 AS account_id FROM dual) s
       ON (a.account_id = s.account_id)
     WHEN MATCHED THEN UPDATE SET a.updated_at = SYSTIMESTAMP;

    EXECUTE IMMEDIATE 'TRUNCATE TABLE tmp_order_ids';
    DBMS_OUTPUT.PUT_LINE('closed ' || v_ids.COUNT || ' orders, statuses ' || v_label);
    log_event('close_month', v_ids.COUNT);
    COMMIT;
  END close_month;
END BILLING_PKG;
/
CREATE OR REPLACE FUNCTION DOCUMENT_PREVIEW(p_order_id IN NUMBER, p_doc_no IN NUMBER) RETURN VARCHAR2 IS
  v_body CLOB;
BEGIN
  SELECT body INTO v_body FROM order_documents WHERE order_id = p_order_id AND doc_no = p_doc_no;
  RETURN DBMS_LOB.SUBSTR(v_body, 200, 1) || CASE WHEN INSTR(v_body, 'iade') > 0 THEN ' [iade]' END;
END DOCUMENT_PREVIEW;
/
CREATE OR REPLACE TRIGGER ORDERS_BIU
BEFORE INSERT OR UPDATE ON ORDERS
FOR EACH ROW
BEGIN
  IF :NEW.note IS NULL THEN
    :NEW.note := NVL2(:OLD.note, :OLD.note, NULL);
  END IF;
  :NEW.updated_at := SYSTIMESTAMP;
END;
/
CREATE OR REPLACE VIEW MONTHS_V AS
SELECT ADD_MONTHS(TRUNC(SYSDATE, 'YYYY'), LEVEL - 1) AS month_start
  FROM dual
CONNECT BY LEVEL <= 12
/
CREATE OR REPLACE TYPE ADDRESS_T AS OBJECT (
  street VARCHAR2(100),
  city   VARCHAR2(40)
)
/
CREATE SYNONYM ORD FOR ORDERS
/
CREATE OR REPLACE PROCEDURE PURGE_REMOTE_COPY IS
BEGIN
  DELETE FROM orders@legacy_link WHERE created_on < ADD_MONTHS(SYSDATE, -84);
  DBMS_SCHEDULER.RUN_JOB('NIGHTLY_PURGE');
END PURGE_REMOTE_COPY;
/
