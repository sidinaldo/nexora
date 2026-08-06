import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { PainelWebhook, ResultadoTeste, SalvarWebhook, SegredoRevelado } from '../modelos';

/** O webhook de SAÍDA — o Nexora avisando um sistema do cliente.
 *
 *  ⚠️ Não confundir com o webhook de ENTRADA (a Evolution avisando o Nexora), que não tem tela:
 *  são direções opostas, com modelos de segurança opostos. */
@Injectable({ providedIn: 'root' })
export class WebhooksServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/webhooks-saida`;

  obter(): Observable<PainelWebhook> {
    return this.http.get<PainelWebhook>(this.base);
  }

  /** Devolve o segredo SÓ na criação; em toda atualização vem nulo. */
  salvar(dados: SalvarWebhook): Observable<{ segredo: SegredoRevelado | null }> {
    return this.http.put<{ segredo: SegredoRevelado | null }>(this.base, dados);
  }

  regerarSegredo(): Observable<SegredoRevelado> {
    return this.http.post<SegredoRevelado>(`${this.base}/segredo`, {});
  }

  remover(): Observable<void> {
    return this.http.delete<void>(this.base);
  }

  /** Dispara um evento de teste e ESPERA a resposta — é o único endpoint que entrega dentro da
   *  requisição, porque a pessoa está olhando o botão. */
  testar(): Observable<ResultadoTeste> {
    return this.http.post<ResultadoTeste>(`${this.base}/testar`, {});
  }

  reenviar(entregaId: number): Observable<void> {
    return this.http.post<void>(`${this.base}/entregas/${entregaId}/reenviar`, {});
  }
}
