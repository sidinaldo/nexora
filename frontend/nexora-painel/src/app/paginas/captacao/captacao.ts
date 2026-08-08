import { Component, OnInit, inject, signal } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { Canais } from '../canais/canais';
import { CanaisServico } from '../../nucleo/servicos/canais.servico';

/** CAPTAÇÃO — de onde os leads vêm.
 *
 *  ===================== POR QUE OS DOIS NUMA TELA SÓ =====================
 *  Formulário do site e QR/link respondem à MESMA pergunta do cliente, compartilham a mesma
 *  estatística e o mesmo modo de uso: criar, publicar em algum lugar, e depois olhar quantos
 *  leads vieram. Em telas separadas, comparar "o panfleto trouxe mais que a landing page?"
 *  exigia abrir duas telas e somar de cabeça.
 *
 *  ===================== E POR QUE NÃO DENTRO DE CONFIGURAÇÕES =====================
 *  Configurações é formulário de AJUSTE — dados da empresa, janela, semáforo, feriados. Captação
 *  é superfície de GESTÃO: tem lista, número por item, código para copiar e arquivo para baixar.
 *  Empilhar as duas faria a tela de ajuste crescer para o dobro e esconderia a captação no fim
 *  de uma página que ninguém rola inteira.
 *
 *  ===================== O QUE ESTE COMPONENTE FAZ, E SÓ =====================
 *  Cabeçalho, resumo e abas. As duas listas continuam sendo os componentes que já existiam —
 *  eles perderam o cabeçalho de página e viraram PAINÉIS. Copiar o conteúdo deles para cá teria
 *  criado uma terceira cópia de regras que já estavam prontas e testadas.
 *  ======================================================================== */
@Component({
  selector: 'app-captacao',
  imports: [Canais],
  templateUrl: './captacao.html',
  styleUrl: './captacao.css'
})
export class Captacao implements OnInit {
  private canaisServico = inject(CanaisServico);

  // ---- o resumo
  leadsCanais = signal(0);
  canaisAtivos = signal(0);
  totalCanais = signal(0);

  carregandoResumo = signal(true);

  ngOnInit() {
    this.carregarResumo();
  }

  /** O resumo dos canais.
   *
   *  Ele buscava DUAS listas — formulários e canais — porque existia para compará-las. Com o
   *  formulário fora da tela sobrou uma, e o `forkJoin` com um ramo só seria cerimônia.
   *
   *  O `catchError` fica: lista que falha vira resumo zerado em vez de tela presa em
   *  "Carregando…", e o painel de canais abaixo mostra o próprio erro. */
  carregarResumo() {
    this.carregandoResumo.set(true);

    this.canaisServico.listar()
      .pipe(catchError(() => of({ itens: [], conexoes: [], podeCriar: false, leadsAtribuidos: 0 })))
      .subscribe(r => {
        this.totalCanais.set(r.itens.length);
        this.canaisAtivos.set(r.itens.filter(c => c.ativo).length);
        this.leadsCanais.set(r.leadsAtribuidos);

        this.carregandoResumo.set(false);
      });
  }
}
