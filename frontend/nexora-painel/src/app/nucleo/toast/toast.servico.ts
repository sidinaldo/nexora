import { Injectable, signal } from '@angular/core';

export type TipoToast = 'info' | 'sucesso' | 'erro';

export interface Toast {
  id: number;
  tipo: TipoToast;
  texto: string;
}

/** Notificação não-bloqueante. O Recupera não tem — lá o padrão é `erro = signal('')` por
 *  página mais a classe `.erro`, que funciona para o resultado de uma ação do usuário.
 *
 *  Com realtime isso não basta: a mensagem chega SEM ninguém ter clicado em nada, e não há
 *  "página" dona daquele aviso. Daí um serviço global, sem biblioteca. */
@Injectable({ providedIn: 'root' })
export class ToastServico {
  readonly toasts = signal<Toast[]>([]);
  private proximoId = 1;

  info(texto: string) { this.mostrar('info', texto); }
  sucesso(texto: string) { this.mostrar('sucesso', texto); }
  erro(texto: string) { this.mostrar('erro', texto, 8000); }

  /** Erros duram mais (8s): quem precisa ler uma falha precisa de mais tempo que quem lê
   *  "mensagem enviada". */
  private mostrar(tipo: TipoToast, texto: string, duracao = 4000) {
    const id = this.proximoId++;
    this.toasts.update(atual => [...atual, { id, tipo, texto }]);
    setTimeout(() => this.fechar(id), duracao);
  }

  fechar(id: number) {
    this.toasts.update(atual => atual.filter(t => t.id !== id));
  }
}
