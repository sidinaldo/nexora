import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';

/** ===================== LPA-1 · QUEM PAROU DE SER TRABALHADO =====================
 *
 *  ⚠️ `negociacaoId`, `pipelineNome` e `etapaNome` SÃO NULOS quando o contato nunca virou negócio
 *  — e esse é o lead frio mais comum, não uma falha de dado. A tela escreve travessão; tratar como
 *  erro esconderia justamente quem mais precisa aparecer.
 *
 *  ⚠️ A GRANULARIDADE É A NEGOCIAÇÃO ABERTA, não o contato. Uma pessoa com negócio aberto em dois
 *  funis rende duas linhas, e é o certo: os dois podem estar parados por motivos diferentes.
 *
 *  ⚠️ ESTES TIPOS NÃO SE RECOPIAM NA TELA. Redeclarar interface do serviço no componente já causou
 *  bug aqui — o `/opcoes` ganhou um campo e a cópia ficou com a forma antiga.
 *  ============================================================================================ */
export interface LeadParado {
  contatoId: number;
  nome: string;
  telefone: string;
  origem: string;
  responsavelId: number | null;
  responsavelNome: string | null;
  negociacaoId: number | null;
  pipelineNome: string | null;
  etapaNome: string | null;
  valor: number | null;
  paradoDesde: string;
  diasParado: number;
}

export interface PaginaLeadsParados {
  itens: LeadParado[];
  total: number;
}

/** As janelas que o servidor aceita. Lista fechada no `JanelasDeParada`: qualquer outro número
 *  volta 400. */
export type JanelaDeParada = 15 | 30 | 60 | 90;

/** O recorte inteiro, num objeto só.
 *
 *  ⚠️ TODOS MENOS `origem` SÃO DA NEGOCIAÇÃO, e ligar qualquer um deles esconde quem não tem
 *  negócio aberto — o lead que ninguém abriu não está em funil nenhum. A tela avisa em vez de
 *  deixar o operador concluir que a lista encolheu sozinha. */
export interface FiltroLeadsParados {
  dias: JanelaDeParada;
  pagina: number;
  responsavelId: number | null;
  pipelineId: number | null;
  etapaId: number | null;
  origem: string | null;
  etiquetaId: number | null;
  valorMin: number | null;
  valorMax: number | null;
}

/** O lembrete em lote.
 *
 *  ⚠️ `contatoIds`, NÃO `negociacaoIds`. A tabela mostra uma linha por negociação aberta, mas o
 *  lembrete é do CONTATO — quem tem negócio em dois funis recebe uma tarefa, não duas. A tela
 *  seleciona por contato por essa razão.
 *
 *  ⚠️ NÃO EXISTE CAMPO DE MENSAGEM, e é de propósito: o WhatsApp roda via Baileys e disparo em
 *  massa queima o número do cliente. Isto cria TAREFA para o vendedor, nunca envio. */
export interface LembreteEmLote {
  contatoIds: number[];
  dataAlvo: string;
  titulo: string;
  observacao: string | null;
}

/** ⚠️ `pulados` É SEPARADO DE `falhou` porque um não é problema e o outro é: pulado é quem já
 *  tinha lembrete pendente — não ganha outro, senão o vendedor recebe a mesma tarefa todo dia. */
export interface ResultadoEmLote {
  criados: number;
  pulados: number;
  falhou: number;
}

@Injectable({ providedIn: 'root' })
export class LeadsParadosServico {
  private http = inject(HttpClient);

  readonly janelas: JanelaDeParada[] = [15, 30, 60, 90];

  /** Cinquenta por página porque a tela existe para AGIR em lote sobre o que está nela — vinte
   *  obrigaria a paginar no meio de uma seleção. */
  readonly porPagina = 50;

  listar(f: FiltroLeadsParados): Observable<PaginaLeadsParados> {
    let p = new HttpParams()
      .set('dias', f.dias)
      .set('pagina', f.pagina)
      .set('tamanho', this.porPagina);

    // ⚠️ SÓ O QUE FOI PREENCHIDO ENTRA, e o teste é `!= null`, não truthy: com truthy, o id 0 e o
    // valor 0 sumiriam da query string. Mandar o parâmetro vazio é pior ainda — o servidor tenta
    // interpretar string vazia e devolve 400.
    const opcionais: [string, unknown][] = [
      ['responsavelId', f.responsavelId],
      ['pipelineId', f.pipelineId],
      ['etapaId', f.etapaId],
      ['origem', f.origem],
      ['etiquetaId', f.etiquetaId],
      ['valorMin', f.valorMin],
      ['valorMax', f.valorMax]
    ];

    for (const [chave, valor] of opcionais) {
      if (valor !== null && valor !== undefined && valor !== '') p = p.set(chave, String(valor));
    }

    return this.http.get<PaginaLeadsParados>(`${API}/leads-parados`, { params: p });
  }

  /** ⚠️ O TETO DO SERVIDOR É `porPagina`, e por isso "selecionar tudo" nesta tela sempre cabe em
   *  uma chamada. Mudar um dos dois números sem o outro deixa a tela oferecendo uma seleção que o
   *  servidor recusa com 400. */
  criarLembretes(pedido: LembreteEmLote): Observable<ResultadoEmLote> {
    return this.http.post<ResultadoEmLote>(`${API}/leads-parados/lembretes`, pedido);
  }
}
