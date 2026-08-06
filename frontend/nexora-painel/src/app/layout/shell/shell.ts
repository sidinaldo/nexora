import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { OnboardingServico } from '../../nucleo/servicos/onboarding.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ToastPilha } from '../../nucleo/toast/toast';
import { StatusPainel } from '../../nucleo/modelos';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastPilha],
  templateUrl: './shell.html',
  styleUrl: './shell.css'
})
export class Shell implements OnInit, OnDestroy {
  auth = inject(AuthServico);
  realtime = inject(RealtimeServico);
  onboarding = inject(OnboardingServico);
  private painel = inject(PainelServico);
  private toast = inject(ToastServico);

  status = signal<StatusPainel | null>(null);

  /** Começa true para não piscar o banner de "WhatsApp desconectado" antes do 1º carregamento. */
  whatsappConectado = signal(true);
  naoLidas = signal(0);

  private timer: ReturnType<typeof setInterval> | null = null;
  private assinaturas: { unsubscribe(): void }[] = [];

  async ngOnInit() {
    await this.realtime.conectar();
    this.carregarStatus();

    // O link "Primeiros passos" só aparece enquanto falta passo. Uma leitura no boot basta: o
    // estado muda por ação do usuário (conectar, convidar) e a tela de primeiros passos
    // recarrega sozinha. Falhar aqui não pode derrubar o shell.
    this.onboarding.carregar().subscribe({ error: () => { } });

    this.assinaturas.push(
      // Mensagem chegando pelo celular do cliente: badge sobe e o toast avisa, mesmo que o
      // vendedor esteja em outra tela.
      this.realtime.mensagemRecebida$.subscribe(m => {
        if (m.direcao === 'entrada') {
          this.naoLidas.update(n => n + 1);
          this.toast.info(`${m.contatoNome}: ${m.previa ?? 'nova mensagem'}`);
        }
      }),
      this.realtime.contatoCriado$.subscribe(c =>
        this.toast.sucesso(`Novo lead pelo WhatsApp: ${c.nome}`)),
      // A queda do número é o aviso mais importante do painel: sem ele o vendedor digita uma
      // resposta que não vai sair.
      //
      // ===================== O EVENTO NÃO DECIDE O BANNER (ARQ-2) =====================
      // O evento fala de UMA conexão; o banner fala da EMPRESA. Com multi-número, aplicar o
      // status do evento direto na flag faria uma conexão voltando ao ar apagar o alerta
      // enquanto outra continua caída — e o vendedor perderia o aviso justamente quando ele
      // ainda vale. Quem sabe o agregado é o servidor, então o evento só pede o status de novo.
      // ===============================================================================
      this.realtime.conexaoMudou$.subscribe(c => {
        if (c.status !== 'conectado') this.toast.erro('Um WhatsApp desconectou. Confira em Conexão.');
        this.carregarStatus();
      })
    );

    // O status também chega por webhook, mas um poll leve mantém badge e banner frescos em
    // qualquer tela — inclusive se o hub cair. 45s basta: não é evento de segundo a segundo.
    this.timer = setInterval(() => this.carregarStatus(), 45_000);
  }

  ngOnDestroy() {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
    this.assinaturas.forEach(a => a.unsubscribe());
    this.realtime.desconectar();
  }

  private carregarStatus() {
    this.painel.status().subscribe({
      next: s => {
        this.status.set(s);
        this.naoLidas.set(s.naoLidas);
        this.whatsappConectado.set(s.whatsappConectado);
      },
      // O status não pode derrubar a tela inteira.
      error: () => { }
    });
  }

  /** O PONTO no item "Conexão" do menu (DES-3).
   *
   *  ===================== UM FATO, DOIS LUGARES COM PAPÉIS DISTINTOS =====================
   *  O ponto INFORMA sempre, junto do link que leva até a coisa. A faixa vermelha no topo do
   *  conteúdo ALERTA, e só no estado crítico — o vendedor não pode digitar uma resposta que não
   *  vai sair. Não é duplicação: é informação contínua contra interrupção pontual.
   *
   *  O que saiu foi o indicador do rodapé, que dizia "sem conexão" para um fato DIFERENTE (o hub
   *  de tempo real) com um texto quase igual ao do banner. Esse sim era dois lugares para o mesmo
   *  papel — pior ainda, para fatos diferentes.
   *
   *  ⚠️ `verificando` NÃO é "conectando". O `StatusPainel` carrega `whatsappConectado` e
   *  `conexoesCaidas`, e nenhum dos dois distingue "pareando agora" de "conectado" — acrescentar
   *  o estado seria mudança de API, que este bloco não faz. O âmbar aqui cobre o intervalo entre
   *  abrir o painel e a primeira resposta chegar, que é quando um ponto verde estaria mentindo.
   *  Ver docs/DES-3.md.
   *  ====================================================================================== */
  statusConexao = computed<'ok' | 'verificando' | 'caiu'>(() => {
    if (this.status() === null) return 'verificando';
    return this.whatsappConectado() ? 'ok' : 'caiu';
  });

  rotuloConexao = computed(() => {
    switch (this.statusConexao()) {
      case 'ok': return 'WhatsApp conectado';
      case 'caiu': return this.tituloDaQueda();
      default: return 'Verificando a conexão…';
    }
  });

  /** O banner diz QUAL número caiu. "WhatsApp desconectado" numa empresa com três números é um
   *  aviso que não diz o que fazer — e a partir de três nomes a lista fica mais longa que o
   *  aviso, então vira contagem. */
  tituloDaQueda = computed(() => {
    const nomes = this.status()?.conexoesCaidas ?? [];
    if (nomes.length === 0) return 'WhatsApp desconectado.';
    if (nomes.length === 1) return `WhatsApp "${nomes[0]}" desconectado.`;
    if (nomes.length === 2) return `WhatsApp "${nomes[0]}" e "${nomes[1]}" desconectados.`;
    return `${nomes.length} números de WhatsApp desconectados.`;
  });

  iniciais(nome: string | undefined): string {
    if (!nome) return '?';
    const p = nome.trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase();
  }
}
