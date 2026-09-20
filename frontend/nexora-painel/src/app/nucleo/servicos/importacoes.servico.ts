import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { PipelinesServico, recontarMenu } from './pipelines.servico';
import {
  ColunaMapeada, GravarImportacao, ImportacaoRecebida, PreviaImportacao, ResultadoImportacao
} from '../modelos';

/** A IMPORTAÇÃO DE LEADS POR ARQUIVO (INT-XX).
 *
 *  ⚠️ O ARQUIVO SOBE UMA VEZ SÓ, e é a diferença para a importação da issue #8: ali ele subia na
 *  prévia e de novo na gravação. Aqui o upload guarda as linhas no servidor, e os passos seguintes
 *  falam pelo `id` — é o que torna possível o arquivo de 10 MB e o processamento em segundo plano.
 *
 *  Os quatro passos são quatro rotas, na ordem em que a tela os usa. */
@Injectable({ providedIn: 'root' })
export class ImportacoesServico {
  private http = inject(HttpClient);
  private pipelines = inject(PipelinesServico);
  private base = `${API}/importacoes`;

  /** Sobe o arquivo. NADA vira contato: volta o que foi achado e o mapeamento sugerido. */
  receber(arquivo: File): Observable<ImportacaoRecebida> {
    const corpo = new FormData();
    corpo.append('arquivo', arquivo);
    return this.http.post<ImportacaoRecebida>(this.base, corpo);
  }

  /** Aplica um mapeamento e devolve o que VAI acontecer. Continua sem gravar nada. */
  prever(id: number, mapeamento: ColunaMapeada[]): Observable<PreviaImportacao> {
    return this.http.post<PreviaImportacao>(`${this.base}/${id}/previa`, { mapeamento });
  }

  /** Grava. Acima do corte do servidor volta `processando`, e quem termina é o job.
   *
   *  ⚠️ `recontarMenu` porque com funil escolhido a importação CRIA CARDS, e o contador ao lado do
   *  funil no menu é carregado uma vez no boot — sem isto ele fica velho até recarregar a página. */
  gravar(id: number, pedido: GravarImportacao): Observable<ResultadoImportacao> {
    return this.http.post<ResultadoImportacao>(`${this.base}/${id}/gravar`, pedido)
      .pipe(recontarMenu(this.pipelines));
  }

  /** Onde ela está — é o que a tela pergunta enquanto o arquivo grande processa. */
  acompanhar(id: number): Observable<ResultadoImportacao> {
    return this.http.get<ResultadoImportacao>(`${this.base}/${id}`);
  }
}
