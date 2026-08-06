import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { Formularios } from '../formularios/formularios';
import { Canais } from '../canais/canais';
import { FormulariosServico } from '../../nucleo/servicos/formularios.servico';
import { CanaisServico } from '../../nucleo/servicos/canais.servico';

export type AbaCaptacao = 'formularios' | 'qr';

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
  imports: [Formularios, Canais],
  templateUrl: './captacao.html',
  styleUrl: './captacao.css'
})
export class Captacao implements OnInit {
  private formulariosServico = inject(FormulariosServico);
  private canaisServico = inject(CanaisServico);
  private rota = inject(ActivatedRoute);
  private router = inject(Router);

  aba = signal<AbaCaptacao>('formularios');

  // ---- o resumo
  leadsFormularios = signal(0);
  formulariosAtivos = signal(0);
  totalFormularios = signal(0);

  leadsCanais = signal(0);
  canaisAtivos = signal(0);
  totalCanais = signal(0);

  carregandoResumo = signal(true);

  total = computed(() => this.leadsFormularios() + this.leadsCanais());

  /** A fatia de cada canal no total. Zero leads = zero, e não NaN — a tela nasce vazia. */
  fatiaFormularios = computed(() =>
    this.total() === 0 ? 0 : Math.round((this.leadsFormularios() / this.total()) * 100));
  fatiaCanais = computed(() => this.total() === 0 ? 0 : 100 - this.fatiaFormularios());

  ngOnInit() {
    // Aba pela URL: `/captacao?aba=qr`. É o que faz o link antigo de QR chegar na aba certa em
    // vez de na primeira, e o que permite mandar "abre em Captação, aba QR" por mensagem.
    const pedida = this.rota.snapshot.queryParamMap.get('aba');
    if (pedida === 'qr' || pedida === 'formularios') this.aba.set(pedida);

    this.carregarResumo();
  }

  trocarAba(aba: AbaCaptacao) {
    if (this.aba() === aba) return;
    this.aba.set(aba);

    // `replaceUrl`: trocar de aba não é navegação para o histórico. Sem isto, o botão "voltar"
    // do navegador percorreria as abas antes de sair da tela.
    this.router.navigate([], {
      relativeTo: this.rota,
      queryParams: { aba: aba === 'formularios' ? null : aba },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  /** Os dois totais, numa leitura só.
   *
   *  ===================== POR QUE O RESUMO BUSCA DE NOVO =====================
   *  A aba aberta já busca a própria lista. O resumo busca as DUAS porque ele existe justamente
   *  para comparar — mostrar só o canal visível seria a mesma tela de antes, com um número a
   *  mais. São dois GET de configuração, com no máximo algumas dezenas de linhas cada.
   *
   *  O alternativo seria o pai carregar tudo e passar para baixo. Custaria transformar os dois
   *  painéis em componentes que não funcionam nem se testam sozinhos — preço alto para poupar
   *  uma requisição numa tela que o dono abre de vez em quando.
   *  ========================================================================= */
  carregarResumo() {
    this.carregandoResumo.set(true);

    // `catchError` por ramo: se a lista de canais falhar, o resumo de formulários continua
    // aparecendo. Um erro num dos dois não pode apagar o outro.
    forkJoin({
      formularios: this.formulariosServico.listar().pipe(catchError(() => of([]))),
      canais: this.canaisServico.listar().pipe(
        catchError(() => of({ itens: [], conexoes: [], podeCriar: false, leadsAtribuidos: 0 })))
    }).subscribe(r => {
      this.totalFormularios.set(r.formularios.length);
      this.formulariosAtivos.set(r.formularios.filter(f => f.ativo).length);
      this.leadsFormularios.set(r.formularios.reduce((s, f) => s + f.leadsRecebidos, 0));

      this.totalCanais.set(r.canais.itens.length);
      this.canaisAtivos.set(r.canais.itens.filter(c => c.ativo).length);
      this.leadsCanais.set(r.canais.leadsAtribuidos);

      this.carregandoResumo.set(false);
    });
  }
}
