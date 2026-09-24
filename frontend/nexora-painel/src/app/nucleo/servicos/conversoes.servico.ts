import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { PainelConversoes, ResultadoTesteConversao, SalvarCredencial } from '../modelos';

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

  reenviar(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/reenviar`, {});
  }
}
