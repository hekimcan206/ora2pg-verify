# 4. PL/SQL incompatibility scan: own lexer and token rules over the dictionary

- Status: accepted
- Date: 2026-11-11

## Context

Before any data moves, the team needs to know which stored code will not run on PostgreSQL and how much
rework it means. ora2pg's assessment (`-t SHOW_REPORT --estimate_cost`) inspects all objects and PL/SQL to
detect code that cannot be converted automatically. It scores difficulty with cost units of 5 minutes by
default ([ora2pg migration cost assessment][ora2pg-cost]). AWS SCT's assessment report lists "action items"
per issue with every instance and a suggested course of action; CONNECT BY, packages and proprietary
functions are the typical complex items ([AWS SCT, Oracle to PostgreSQL][sct]). Industry estimates put
automatic PL/SQL conversion well below DDL conversion, so the manual part is what needs estimating.

## Options considered

1. **Run ora2pg itself.** It is the reference tool, and its report would be authoritative. It needs Perl,
   DBD::Oracle and an Oracle client in the image. Its output is a separate report, and it does not connect the
   code findings to this tool's data comparison.
2. **A full PL/SQL grammar** (ANTLR's PlSql grammar). It is precise, but large, slow to build, and it accepts
   everything, so rules still have to be written on top. It is also brittle on invalid objects, which are
   exactly the ones worth reporting.
3. **A small lexer plus token-pattern rules**, over `DBA_SOURCE`/`ALL_SOURCE`, `ALL_VIEWS` (LONG text) and
   dictionary facts (types, sequences, synonyms, links, temporary tables, materialized views).

## Decision

Option 3, positioned as a complement to ora2pg rather than a replacement:

- The lexer removes comments and string literals (including q-quoted literals) so that `-- CONNECT BY`
  or `'ROWNUM'` in a message never produce findings. It keeps optimizer hints visible and tracks line numbers.
- Each rule names the construct, a severity, the usual PostgreSQL rewrite and a reference: CONNECT BY,
  SYS_CONNECT_BY_PATH, ROWNUM, DECODE (with its NULL = NULL match), NVL/NVL2, `(+)` joins, PRAGMA
  AUTONOMOUS_TRANSACTION, SYSDATE/SYSTIMESTAMP semantics, the `''` literal, MINUS, `seq.NEXTVAL`, MERGE,
  BULK COLLECT/FORALL, EXECUTE IMMEDIATE, `@dblink`, LISTAGG, RAISE_APPLICATION_ERROR, SQLCODE, date
  arithmetic functions, INSTR, COMMIT/ROLLBACK in stored code, `:NEW/:OLD`, and 17 supplied packages
  (DBMS_*, UTL_*) rated one by one.
- Package-level variables (session state) are found heuristically: declarations between the package
  header and the first routine, with constants told apart. They are reported per declaration with a line
  number for a human to confirm.
- Effort points (Low 1, Medium 3, High 8) rank objects. They are relative, in the spirit of ora2pg's units,
  and deliberately not presented as person-days.
- Snippets in the report replace string literal contents with `'…'` by default, because stored code can
  contain credentials or personal data (`--show-literals` to opt out).
- The scan reads `DBA_` views when the account can (SELECT_CATALOG_ROLE), otherwise `ALL_` views, and says
  which one it used.

## Consequences

- No Perl or Oracle client in the image. The scan runs in about a second on the sample schema and is pinned by a
  golden file. An integration test checks that scanning the live dictionary yields exactly the findings of
  scanning the script offline.
- Token rules can produce false positives (`LEVEL` alone is not flagged for that reason) and false negatives
  (dynamic SQL built from strings is not analysed). The README says so. This is an inventory, and does not
  claim to be a converter.
- The rule catalogue is small and readable. Adding a rule is one entry plus a unit test (CONTRIBUTING.md).

[ora2pg-cost]: https://ora2pg.darold.net/docs/configuration/migration-cost-assessment
[sct]: https://docs.aws.amazon.com/dms/latest/oracle-to-aurora-postgresql-migration-playbook/chap-oracle-aurora-pg.tools.awssct.html
