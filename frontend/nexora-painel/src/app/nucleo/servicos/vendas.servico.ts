import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { VendaDto } from '../modelos';

/** O HISTÓRICO de vendas de um contato (NEG-1).
 *
 *  Separado do `ContatosServico` porque responde uma pergunta diferente: aquele trata do estado
 *  atual do contato, este do que já aconteceu. Foi a confusão entre as duas que produziu o
 *  defeito — reabrir um contato apagava a venda anterior do faturamento. */
@Injectable({ providedIn: 'root' })
export class VendasServico {
  private http = inject(HttpClient);

  doContato(contatoId: number): Observable<VendaDto[]> {
    return this.http.get<VendaDto[]>(`${API}/contatos/${contatoId}/vendas`);
  }

  /** Desfazer uma venda marcada por engano. NÃO apaga: marca. POST e não DELETE porque o verbo
   *  descreve o que acontece de verdade — a linha continua lá, riscada. */
  cancelar(id: number): Observable<void> {
    return this.http.post<void>(`${API}/vendas/${id}/cancelar`, {});
  }
}
