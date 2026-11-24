/*
 * 能力矩阵（前端侧）。
 *
 * 读的是仓库根的 `contracts/transfer-state-machine.json`——**和服务端、手机端
 * 同一份文件**，构建时被 vite 打进 bundle。改语义只改那一份，
 * 三端的测试会同时红（服务端见 TransferStateMachineContractTests）。
 *
 * 以前这些判断散在页面各处：`isActive(row.state)`、`state === 'paused'`、
 * 行内按钮的 `when`……改一条语义要翻好几处，漏一处就是"某端还能点、另一端已经不给"。
 * 现在统一走这里：**服务端接不接受**（accepts）与**界面上显不显示**（shows）
 * 是两个问题，后者可以更严格（D-060）。
 */
import contract from '../../../../contracts/transfer-state-machine.json';

export type TransferAction = 'pause' | 'resume' | 'cancel' | 'retry' | 'deleteRecord';

interface Rule {
  ui?: boolean;
  outcome?: string;
  error?: string;
  note?: string;
}

const table = (contract as { actions: Record<string, Record<string, Rule | string>> }).actions;

function ruleOf(action: TransferAction, state: string): Rule | null {
  const rules = table[action];
  if (!rules) {
    return null;
  }

  const rule = rules[state];
  // 动作里除了状态格还有 note 之类的字符串元数据，只认对象。
  return rule && typeof rule === 'object' ? rule : null;
}

/** 界面上是否**显示**这个动作。 */
export function shows(action: TransferAction, state: string): boolean {
  return ruleOf(action, state)?.ui === true;
}

/** **这一条记录**服务端接不接受（`rejected` 表示"批量里这一条被跳过"，算不接受）。 */
export function accepts(action: TransferAction, state: string): boolean {
  return ruleOf(action, state)?.outcome === 'ok';
}

/** 服务端拒绝时的错误码，用于提示文案。 */
export function errorFor(action: TransferAction, state: string): string | null {
  return ruleOf(action, state)?.error ?? null;
}

/**
 * 一批任务能不能做某个动作。
 *
 * 批量的语义是"**全都**可以才给按钮"：只要有—条还在传输，
 * 「删除记录」就不该出现（点下去只会被跳过几条，用户以为删了其实没删）。
 */
export function allShow(action: TransferAction, states: readonly string[]): boolean {
  return states.length > 0 && states.every((state) => shows(action, state));
}

/** 一批里是否**至少有一条**能做某个动作（用于"这批里还有能取消的"）。 */
export function anyShow(action: TransferAction, states: readonly string[]): boolean {
  return states.some((state) => shows(action, state));
}
