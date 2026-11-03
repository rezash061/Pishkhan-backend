# 0012. Industry templates: a Templates module, keys kept by the receiving modules, apply adds and never overwrites

Status: accepted (2026-11-03)

The backend plan lists `industry_templates` under the `backoffice` schema. Templates are read by the panel (list, preview, apply) as well as edited in the back office, and the Backoffice module only runs in the AdminApi host. So templates are their own module (`templates` schema), loaded by the Api, the AdminApi and the Migrator, and the Backoffice module stays staff-only.

A template version is a row, like a plan: one draft and one published version per industry at a time (two filtered unique indexes), and publishing a draft archives the version it replaces in the same transaction. The content is one JSON document in the panel API's own shapes (FAQ question and answer, flow graph, rule trigger and actions, agent profile), so staff edit exactly what a workspace would get and the existing validators check it. The only template-specific shape is a `start_flow` action that names its flow by the template's `flowKey`, because flow ids exist only inside a workspace. Card sets, post ids and flow ids are refused for the same reason. A draft with issues can be saved (the editor shows them) but not published.

The five launch verticals ship as built-in templates (embedded `Seeds/*.json`). The Migrator adds one only for an industry that has no template yet, so staff edits are never overwritten by a deploy.

Applying is done by the modules that own the data, through their contracts: Knowledge adds FAQs, Automation adds flows and rules, Agent sets the profile. Each item carries its template key, and the key is kept when the owner deletes the item (`knowledge.template_items`, `template_key` on flows and rules with a unique index per workspace). That makes apply idempotent and safe to repeat after a partial failure (three databases contexts, no shared transaction), and it means a template never brings back something the owner removed or overwrites something the owner edited. The agent profile is set only when the workspace has none.

What a template adds must not act before the owner has checked it, because answers contain blanks such as «[مبلغ] تومان»: FAQs and rules are added switched off. Flows are published, since a flow does nothing until a rule starts it.
