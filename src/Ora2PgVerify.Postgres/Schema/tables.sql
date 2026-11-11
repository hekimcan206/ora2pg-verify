-- Target schema as a migration tool might have produced it (owner app_owner, schema app).
-- Deliberate, semantics-preserving representation differences: CHAR(10) became varchar(10) (padding not stored),
-- TIMESTAMP WITH TIME ZONE became timestamptz (offset not stored), CUSTOMERS.BIRTH_DATE became date.
DROP TABLE IF EXISTS app.orders, app.customers, app.order_documents, app.accounts;

CREATE TABLE app.orders (
  order_id       bigint         PRIMARY KEY,
  customer_code  varchar(10)    NOT NULL,
  status         varchar(20)    NOT NULL,
  note           varchar(200),
  amount         numeric(14,2)  NOT NULL,
  discount_rate  numeric(5,4)   NOT NULL,
  created_on     timestamp(0)   NOT NULL,
  shipped_at     timestamp(6),
  updated_at     timestamptz(6) NOT NULL
);

CREATE TABLE app.customers (
  customer_code  varchar(12)    PRIMARY KEY,
  full_name      varchar(100)   NOT NULL,
  city           varchar(40)    NOT NULL,
  credit_limit   numeric(12,2)  NOT NULL,
  birth_date     date           NOT NULL,
  segment        char(1)        NOT NULL
);

CREATE TABLE app.order_documents (
  order_id       bigint         NOT NULL,
  doc_no         smallint       NOT NULL,
  body           text           NOT NULL,
  created_at     timestamp(6)   NOT NULL,
  PRIMARY KEY (order_id, doc_no)
);

CREATE TABLE app.accounts (
  account_id     bigint         PRIMARY KEY,
  balance        numeric(14,2)  NOT NULL,
  status         varchar(20)    NOT NULL,
  updated_at     timestamp(6)   NOT NULL
);
