import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from './api';

/**
 * 轮询一个本机接口。
 *
 * 传 `intervalMs = 0` 表示只读一次（设置页那种静态快照）。
 * 失败时保留上一次的数据并把错误摆出来 —— 界面不该因为一次网络抖动整屏清空。
 */
export function useApi<T>(path: string, intervalMs = 2000) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    let timer: number | undefined;

    const load = async () => {
      try {
        const next = await api.get<T>(path);
        if (!alive.current) {
          return;
        }

        setData(next);
        setError(null);
      } catch (caught) {
        if (!alive.current) {
          return;
        }

        setError(caught instanceof Error ? caught.message : String(caught));
      }
    };

    void load();
    if (intervalMs > 0) {
      timer = window.setInterval(load, intervalMs);
    }

    return () => {
      alive.current = false;
      if (timer) {
        window.clearInterval(timer);
      }
    };
  }, [path, intervalMs, nonce]);

  const reload = useCallback(() => setNonce((n) => n + 1), []);

  return { data, error, reload };
}
