import { Injectable, signal } from '@angular/core';

/** Contagem regressiva do bloqueio de login (rate limit 429). O interceptor lê o header
 *  `Retry-After` e chama `iniciar()`; a tela desabilita o botão enquanto `segundos() > 0`
 *  e mostra a contagem. Zera sozinho.
 *
 *  O header vem exposto pelo CORS (`WithExposedHeaders("Retry-After")` no Program.cs) —
 *  sem isso o navegador não deixaria o Angular lê-lo. */
@Injectable({ providedIn: 'root' })
export class ThrottleLogin {
  readonly segundos = signal(0);
  private timer: ReturnType<typeof setInterval> | null = null;

  iniciar(segundos: number) {
    this.parar();
    const s = Math.max(0, Math.floor(segundos || 0));
    this.segundos.set(s);
    if (s <= 0) return;
    this.timer = setInterval(() => {
      const restante = this.segundos() - 1;
      this.segundos.set(restante);
      if (restante <= 0) this.parar();
    }, 1000);
  }

  private parar() {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
  }
}
