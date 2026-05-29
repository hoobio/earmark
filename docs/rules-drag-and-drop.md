# Spec: drag-and-drop for rule conditions & actions

Handoff spec for implementing drag-and-drop of conditions and actions on the Rules page. Written for an agent picking this up cold. Read [AGENTS.md](../AGENTS.md) first for the build/run loop and conventions.

## Goal

On the Rules page, let the user drag:

- **Conditions** to reorder within a rule, and to move into another rule's condition list.
- **Actions** to reorder within a rule, to move between the main **Actions** list and the **Otherwise** (else) list of the same rule, and to move into another rule's Actions or Otherwise list.

**Type enforcement (hard requirement):** a condition may only land in a condition list; an action may only land in an action list (either Actions or Otherwise). A condition dropped on an action list (or vice-versa) must be rejected with a no-drop cursor and no change.

**Commit semantics (decided by the product owner): commit immediately.** A drop persists and applies at once - like reordering the rule list - it does NOT go through the Save-when-dirty buffer. A cross-rule move persists BOTH affected rules immediately.

## Current architecture (what exists today)

### UI - `src/Earmark.App/Views/RulesPage.xaml` (+ `.xaml.cs`)
- The rule list is a `ListView` named `RulesList` with `CanReorderItems/CanDragItems/AllowDrop` that reorders `RoutingRule`s. Handlers `OnRulesDragStarting` / `OnRulesDragCompleted` collapse the dragged rule for a tidy drag. Reorder is persisted in `RulesViewModel.OnItemsCollectionChanged` → `IRulesService.ReorderAsync`.
- Each rule card is the `RulesList` item template (`x:DataType="vm:RuleRow"`). The expanded editor contains three lists, currently **`ItemsControl`s** (no drag):
  - **Conditions:** `<ItemsControl ItemsSource="{x:Bind Conditions}">` with an **inline** `ConditionRow` `DataTemplate` (combo for type, combo for flow, pattern box, remove button → `OnRemoveConditionClicked`).
  - **Actions:** `<ItemsControl ItemsSource="{x:Bind Actions}" ItemTemplate="{StaticResource ActionRowTemplate}" />`.
  - **Otherwise:** `<ItemsControl ItemsSource="{x:Bind ElseActions}" ItemTemplate="{StaticResource ActionRowTemplate}" />`, wrapped in a panel visible when `ShowElse` (i.e. the rule has ≥1 condition).
- **`ActionRowTemplate`** is a shared keyed `DataTemplate` in `Page.Resources` (`x:DataType="vm:ActionRow"`). Its remove button calls `OnRemoveActionClicked`, which routes to `RemoveActionCommand` or `RemoveElseActionCommand` by checking which collection the row is in (`rule.ElseActions.Contains(row)`).
- Helper `FindAncestorRuleRow(DependencyObject)` walks the visual tree to the owning `RuleRow` (via `FrameworkElement.DataContext`). Reuse it.

### View-models - `src/Earmark.App/ViewModels/RuleRow.cs`
- `RuleRow` owns `ObservableCollection<ConditionRow> Conditions`, `ObservableCollection<ActionRow> Actions`, `ObservableCollection<ActionRow> ElseActions`.
- `ConditionRow` / `ActionRow` are child VMs. **Each is constructed with a `NotifyChildChanged` callback that closes over its owning `RuleRow`** (`new ActionRow(model, NotifyChildChanged)`). This callback is how a child edit marks the parent dirty / refreshes `DisplayName`.
- `ConditionRow.ToCondition()` and `ActionRow.ToAction()` produce the `RuleCondition` / `RuleAction` model for the row's current (live) state.
- Computed flags that must be re-notified after collection changes: `HasConditions`, `HasActions`, `HasElseActions`, `ShowElse` (= `HasConditions`), `DisplayName`.
- **Explicit-save model (already in place):** edits buffer in the row; `IsDirty` + `SaveCommand`/`RevertCommand`; nothing persists until Save **except** the enable toggle, which commits immediately via `CommitEnabledAsync` (persists only the enabled bit against the last-saved snapshot). The dirty baseline is `_savedRule` + `_savedJson`, reset in `SyncFromRule` and after a save.

### Persistence + list rebuild - `src/Earmark.App/ViewModels/RulesViewModel.cs`, `src/Earmark.Core/Services/RulesService.cs`
- `IRulesService`: `UpsertAsync`, `InsertAsync(rule, index)`, `DeleteAsync`, `ReorderAsync`. All raise `RulesChanged`.
- `RulesViewModel.OnRulesChanged`: if `SequenceMatches(Items, _rules.Rules)` (same Ids, same order) → just `QueueMatchRefresh()`, **no rebuild**. On structural change it rebuilds `Items` but **reuses existing `RuleRow` objects by Id** (preserves unsaved edits and expanded state); only genuinely new rules get a fresh row.
- **Implication for drag:** a move only changes rule *contents*, not the set/order of rules, so persisting the affected rule(s) raises `RulesChanged` → `SequenceMatches` stays true → no `Items` rebuild → the `RuleRow` objects survive. Good.

## Gotchas (read before coding)

1. **Child `NotifyChildChanged` is bound to the owning rule.** Moving a row *object* into another rule's collection leaves it notifying the wrong rule. Therefore:
   - **Same-rule moves** (reorder, or Actions↔Otherwise within one rule): relocate the **existing** row object (its parent is still correct).
   - **Cross-rule moves:** **remove + dispose** the source row and **create a NEW** `ConditionRow`/`ActionRow` from `row.ToCondition()`/`row.ToAction()` wired to the **target** rule's `NotifyChildChanged`.
2. **Commit-immediately needs a persist primitive.** Add `RuleRow.PersistNowAsync()`: `await _persistAsync(ToRule()); _savedRule = rule; _savedJson = Serialize(rule); IsDirty = false;`. Cross-rule moves call it on **both** rules.
3. **Nested ListViews.** The condition/action lists sit inside `RulesList` (itself a drag-enabled `ListView`). Verify that starting a drag on an inner item drags the *item*, not the rule card, and that the inner list handles the drop. `RulesList` has no custom `Drop` and its built-in reorder only accepts `RuleRow` items, so an `ActionRow`/`ConditionRow` won't be absorbed by the outer list - but **test this**, it's the highest-risk interaction.
4. **A `DataPackage` payload is required** for cross-`ListView` drops to be accepted: set `e.Data.RequestedOperation = DataPackageOperation.Move` and `e.Data.SetText(...)` in `DragItemsStarting`. Carry the real dragged object in a page field (in-process drag); don't try to serialize the VM.
5. **`DisplayName`** derives from `Actions`; refresh it after action moves.

## Recommended implementation

Convert the three `ItemsControl`s to `ListView`s. **Do not use `CanReorderItems`** - it only handles intra-list moves and fights manual cross-list `Drop`. Handle every move via a manual `Drop` so internal and cross-list are uniform.

### XAML (per list)
- `SelectionMode="None"`, `CanDragItems="True"`, `AllowDrop="True"`.
- `ItemContainerStyle`: `HorizontalContentAlignment=Stretch`, `Padding=0`, `Margin=0` (match the current stacked look; the row Borders already carry `Margin="0,4,0,0"`).
- Conditions list: `DragItemsStarting="OnConditionDragStarting"`, `DragOver="OnConditionDragOver"`, `Drop="OnConditionDrop"`, keep the inline `ConditionRow` template.
- Actions list: `Tag="Actions"`, `DragItemsStarting="OnActionDragStarting"`, `DragOver="OnActionDragOver"`, `Drop="OnActionDrop"`, `ItemTemplate="{StaticResource ActionRowTemplate}"`.
- Otherwise list: `Tag="Else"`, same three handlers, same template.

### Page code-behind (`RulesPage.xaml.cs`)
```csharp
private enum DragKind { Condition, Action }
private sealed record DragContext(RuleRow SourceRule, object Row, DragKind Kind, bool FromElse);
private DragContext? _itemDrag;
```
- `On{Condition,Action}DragStarting(object sender, DragItemsStartingEventArgs e)`: resolve the source rule via `FindAncestorRuleRow((ListView)sender)`; stash `_itemDrag` (for actions, `FromElse = (string?)((ListView)sender).Tag == "Else"`); `e.Data.RequestedOperation = DataPackageOperation.Move; e.Data.SetText("earmark:condition|action");`.
- `On{Condition,Action}DragOver(object sender, DragEventArgs e)`: `e.AcceptedOperation = _itemDrag?.Kind == <matching kind> ? DataPackageOperation.Move : DataPackageOperation.None;`.
- `On{Condition,Action}Drop(object sender, DragEventArgs e)`: resolve target rule; `toElse = (string?)((ListView)sender).Tag == "Else"`; `index = GetDropIndex((ListView)sender, e)`; `await target.AcceptConditionAsync(...)` / `AcceptActionAsync(...)`; set `_itemDrag = null`.
- `GetDropIndex(ListView list, DragEventArgs e)`: `var y = e.GetPosition(list).Y;` walk `i in 0..Items.Count`, `list.ContainerFromIndex(i) as ListViewItem`, `container.TransformToVisual(list).TransformBounds(new Windows.Foundation.Rect(0,0,container.ActualWidth,container.ActualHeight))`; return first `i` whose `Top + Height/2 > y`, else `Items.Count`.

### `RuleRow` primitives
```csharp
public async Task PersistNowAsync() { /* persist ToRule(), reset _savedRule/_savedJson, IsDirty=false */ }

// index is an insertion point 0..Count from GetDropIndex.
public async Task AcceptConditionAsync(ConditionRow row, RuleRow source, int index)
{
    if (source == this) { /* remove at old idx, reinsert SAME object at adjusted idx (from<index ? index-1 : index) */ }
    else { var m = row.ToCondition(); source.RemoveConditionForMove(row);
           Conditions.Insert(Clamp(index,0,Conditions.Count), new ConditionRow(m, NotifyChildChanged)); }
    NotifyConditionsChanged();                 // HasConditions, ShowElse, DisplayName
    await PersistNowAsync();
    if (source != this) await source.PersistNowAsync();
}

public async Task AcceptActionAsync(ActionRow row, RuleRow source, bool sourceElse, bool targetElse, int index)
{
    var target = targetElse ? ElseActions : Actions;
    if (source == this && sourceElse == targetElse) { /* relocate SAME object within `target` (index adjust) */ }
    else if (source == this) { var src = sourceElse ? ElseActions : Actions; src.Remove(row);
                               target.Insert(Clamp(index,0,target.Count), row); /* same object, parent ok */ }
    else { var m = row.ToAction(); source.RemoveActionForMove(row, sourceElse);
           target.Insert(Clamp(index,0,target.Count), new ActionRow(m, NotifyChildChanged)); }
    NotifyActionsChanged();                     // HasActions, HasElseActions, DisplayName
    await PersistNowAsync();
    if (source != this) await source.PersistNowAsync();
}

internal void RemoveConditionForMove(ConditionRow row) { if (Conditions.Remove(row)) { row.Dispose(); NotifyConditionsChanged(); } }
internal void RemoveActionForMove(ActionRow row, bool fromElse) { var l = fromElse?ElseActions:Actions; if (l.Remove(row)) { row.Dispose(); NotifyActionsChanged(); } }
```
`NotifyConditionsChanged` / `NotifyActionsChanged` raise `OnPropertyChanged` for the relevant flags + `DisplayName`.

## Acceptance criteria
- Reorder a condition within a rule → order changes, persists, applies immediately (no Save needed).
- Reorder actions within Actions, and within Otherwise.
- Drag an action from Actions → Otherwise (same rule) and back.
- Drag a condition into another rule's condition list; drag an action into another rule's Actions or Otherwise list → both rules persist + the routing applier re-applies.
- A condition cannot drop on an action list, nor an action on a condition list (no-drop cursor; no change).
- After a **cross-rule** move, editing the moved row marks the **target** rule dirty (proves the child callback was rewired).
- The outer rule-list reorder still works and is not triggered by inner item drags.
- No duplicated or lost rows after any move; `DisplayName`, `HasConditions`/`HasActions`/`ShowElse`, and the "Unsaved" badge stay correct.

## Notes
- Match-preview/status refreshes via `RulesViewModel.RefreshMatchesAsync` after `RulesChanged`; no extra wiring.
- Verify per the AGENTS.md loop (kill → build x64 → relaunch → read log). Drag UX must be checked by hand in the running app; logic-only build success is not sufficient here.
- Keep it Fluent/native: `SelectionMode=None`, no selection chrome; the existing `EmphasizedSubtleCardStyle`/`SubtleCardStyle` row borders provide the visual.
