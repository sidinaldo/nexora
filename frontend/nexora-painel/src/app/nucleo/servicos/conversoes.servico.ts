import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import {
  PainelConversoes, ResultadoEnvioEmLote, ResultadoTesteConversao, ResumoConversoes, SalvarCredencial
} from '../modelos';

/** CONVERSÕES DE ANÚNCIO (INT-4) — o Nexora avisando a Meta de que um lead virou venda.
 *
 *  ⚠️ Nenhum método devolve o token. O `GET` traz sufixo mascarado, e salvar com o campo vazio
 *  mantém o que já estava lá — trocar o Pixel ID não pode ser um jeito de apagar o token por
 *  descuido. */
@Injectable({ providedIn: 'root' })
export class ConversoesServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/conversoes`;

  obter(): Observable<PainelConversoes> {
    return this.http.get<PainelConversoes>(this.base);
  }

  /** Só as duas contas do aviso. Rota própria para a Captação não carregar as 50 últimas
   *  conversões só para desenhar uma frase. */
  resumo(): Observable<ResumoConversoes> {
    return this.http.get<ResumoConversoes>(`${this.base}/resumo`);
  }

  salvar(dados: SalvarCredencial): Observable<void> {
    return this.http.put<void>(this.base, dados);
  }

  remover(): Observable<void> {
    return this.http.delete<void>(this.base);
  }

  /** Manda um evento de teste e ESPERA a resposta — é o único endpoint deste bloco que entrega
   *  dentro da requisição, porque a pessoa está olhando o botão. */
  testar(): Observable<ResultadoTesteConversao> {
    return this.http.post<ResultadoTesteConversao>(`${this.base}/testar`, {});
  }

  /** INT-5 · põe na fila a conversão de uma VENDA que nunca virou evento.
   *
   *  ⚠️ O id aqui é de uma NEGOCIAÇÃO; no `reenviar` abaixo é de um EVENTO. A rota separa os dois
   *  debaixo de `vendas/` justamente para a diferença ficar visível antes de alguém confundi-los. */
  enviarVenda(negociacaoId: number): Observable<void> {
    return this.http.post<void>(`${this.base}/vendas/${negociacaoId}/enviar`, {});
  }

  /** INT-5 · as que ainda cabem nos 7 dias, até o teto de uma rodada do motor. */
  enviarPendentes(): Observable<ResultadoEnvioEmLote> {
    return this.http.post<ResultadoEnvioEmLote>(`${this.base}/vendas/enviar-pendentes`, {});
  }

  reenviar(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/reenviar`, {});
  }
}
